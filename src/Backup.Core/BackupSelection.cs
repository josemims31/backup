using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Backup.Core
{
    public sealed class ExclusionRule
    {
        public bool Enabled = true;
        public string Kind;
        public string Pattern;
        public string Description;
    }
    public sealed class BackupExclusions
    {
        private sealed class Matcher
        {
            internal ExclusionRule Rule;internal Regex Regex;internal string Literal,Prefix,Suffix;
            private static bool CouldMatchLiteral(string path,int start,int length,string literal,bool suffix)
            {
                if(literal==null)return true;if(length<literal.Length)return false;int offset=suffix?start+length-literal.Length:start;
                for(int index=0;index<literal.Length;index++){char actual=path[offset+index],expected=literal[index];if(actual>=128)continue;if(actual>='A'&&actual<='Z')actual=(char)(actual+32);if(expected>='A'&&expected<='Z')expected=(char)(expected+32);if(actual!=expected)return false;}return true;
            }
            internal bool Matches(string path,int start,int length)
            {
                if(Literal==null){if(!CouldMatchLiteral(path,start,length,Prefix,false))return false;if(!CouldMatchLiteral(path,start,length,Suffix,true)&&!(length>0&&path[start+length-1]=='\n'&&CouldMatchLiteral(path,start,length-1,Suffix,true)))return false;return Regex.Match(path,start,length).Success;}
                // Regex '$' also accepts a final LF. Non-ASCII input uses the original
                // invariant regex case folding rather than a different string comparer.
                int size=length;if(size==Literal.Length+1&&path[start+size-1]=='\n')size--;
                if(size!=Literal.Length)return false;
                for(int index=0;index<size;index++){
                    char actual=path[start+index],expected=Literal[index];
                    if(actual>=128)return Regex.Match(path,start,length).Success;
                    if(actual>='A'&&actual<='Z')actual=(char)(actual+32);if(expected>='A'&&expected<='Z')expected=(char)(expected+32);
                    if(actual!=expected)return false;
                }
                return true;
            }
        }
        private readonly List<Matcher> rules = new List<Matcher>();
        public static List<ExclusionRule> NormalizeRules(IEnumerable<ExclusionRule> configured){return configured.Select(r=>new ExclusionRule{Enabled=r.Enabled,Kind=new[]{"FileName","DirectoryName","PathPattern"}.Contains(r.Kind)?"Wildcard":r.Kind,Pattern=r.Pattern,Description=r.Description=="$RECYCLE.BIN"||r.Description=="#recycle"?"回收站":r.Description=="Verysync 全部管理数据"||r.Description==".verysync-localsync"?"微力同步":r.Description}).ToList();}
        public BackupExclusions(IEnumerable<ExclusionRule> configured)
        {
            foreach (ExclusionRule rule in NormalizeRules(configured)){
                if(!rule.Enabled)continue;
                if(string.IsNullOrWhiteSpace(rule.Pattern)||!new[]{"Wildcard","Regex"}.Contains(rule.Kind))throw new ArgumentException("排除规则的类型或内容无效。");
                string pattern=rule.Kind=="Regex"?rule.Pattern:Environment.ExpandEnvironmentVariables(rule.Pattern).Replace('/', '\\');
                string expression=rule.Kind=="Regex"?pattern:"^"+Regex.Escape(pattern).Replace("\\*",".*").Replace("\\?",".")+"$";
                var regex=new Regex(expression,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));var matcher=new Matcher{Rule=rule,Regex=regex,Literal=rule.Kind!="Regex"&&pattern.IndexOfAny(new[]{'*','?'})<0&&pattern.All(value=>value<128)?pattern:null};
                if(rule.Kind!="Regex"&&matcher.Literal==null&&pattern.All(value=>value<128)){int first=pattern.IndexOfAny(new[]{'*','?'}),last=Math.Max(pattern.LastIndexOf('*'),pattern.LastIndexOf('?'));matcher.Prefix=pattern.Substring(0,first);matcher.Suffix=pattern.Substring(last+1);}
                rules.Add(matcher);
            }
        }
        public string Match(string path)
        {
            string ignored;return MatchCore(path,false,out ignored);
        }
        // A non-null prefix ends at a directory separator and proves that every
        // descendant has the same excluded candidate. A file-only hit never prunes siblings.
        public string Match(string path,out string excludedDirectoryPrefix)
        {return MatchCore(path,true,out excludedDirectoryPrefix);}
        private string MatchCore(string path,bool identifyDirectory,out string excludedDirectoryPrefix)
        {
            path=path.Replace('/', '\\');excludedDirectoryPrefix=null;
#if UI_TEST
            MatchCalls++;
#endif
            foreach(var match in rules)try{
                int start=0;
                while(start<path.Length){if(path[start]=='\\'){start++;continue;}int end=path.IndexOf('\\',start);if(end<0)end=path.Length;
                    if(match.Matches(path,start,end-start)){if(identifyDirectory&&end<path.Length)excludedDirectoryPrefix=path.Substring(0,end+1);return match.Rule.Description??match.Rule.Pattern;}start=end+1;
                }
                if(match.Matches(path,0,path.Length))return match.Rule.Description??match.Rule.Pattern;
                for(int end=path.IndexOf('\\');end>=0;end=path.IndexOf('\\',end+1))if(end>0&&match.Matches(path,0,end)){if(identifyDirectory)excludedDirectoryPrefix=path.Substring(0,end+1);return match.Rule.Description??match.Rule.Pattern;}
            }catch(RegexMatchTimeoutException){throw new ArgumentException("排除规则匹配超时，请简化正则表达式："+match.Rule.Pattern);}
            return null;
        }
#if UI_TEST
        public int MatchCalls;
#endif
        public static List<ExclusionRule> Defaults()
        {
            var items = new List<ExclusionRule>();
            items.Add(new ExclusionRule { Kind="Wildcard",Pattern="~$*",Description="office临时文件" });
            items.Add(new ExclusionRule { Kind="Wildcard",Pattern="*.dwl",Description="AutoCAD临时文件" });
            items.Add(new ExclusionRule { Kind="Wildcard",Pattern="*.dwl2",Description="AutoCAD临时文件" });
            items.Add(new ExclusionRule { Kind="Wildcard",Pattern="*.idlk",Description="InDesign临时文件" });
            items.Add(new ExclusionRule { Kind="Wildcard",Pattern=".~lock.*#",Description="LibreOffice临时文件" });
            items.Add(new ExclusionRule { Kind="Wildcard",Pattern="Thumbs.db",Description="Windows 缩略图" });
            items.Add(new ExclusionRule { Kind="Wildcard",Pattern=@"%LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache_*.db",Description="Windows 缩略图缓存" });
            foreach(string name in new[]{"$RECYCLE.BIN",".verysync",".verysync-localsync","#recycle","@eaDir"})
                items.Add(new ExclusionRule { Kind="Wildcard",Pattern=name,Description=name==".verysync"||name==".verysync-localsync"?"微力同步":name=="$RECYCLE.BIN"||name=="#recycle"?"回收站":name });
            return items;
        }
    }
    public sealed class SelectionRule
    {
        public string Path;
        public bool Directory;
        public bool Include;
    }
    public sealed class FileSelection
    {
        public List<SelectionRule> Rules = new List<SelectionRule>();
        private Dictionary<string,SelectionRule> index;
        private string identity;
        private int indexedCount=-1;
        private static string Canonical(string path) { if(path!=null && path.Length==2 && path[1]==':')path+="\\";return System.IO.Path.GetFullPath(path).TrimEnd('\\'); }
        public bool Includes(string path)
        {
            path=Canonical(path);
            if(index==null||indexedCount!=Rules.Count){index=new Dictionary<string,SelectionRule>(StringComparer.OrdinalIgnoreCase);foreach(var rule in Rules)if(!index.ContainsKey(rule.Path))index.Add(rule.Path,rule);indexedCount=Rules.Count;}
            bool exact=true;
            while(true){SelectionRule rule;if(index.TryGetValue(path,out rule)&&(exact||rule.Directory))return rule.Include;int slash=path.LastIndexOf('\\');if(slash<=1)break;path=path.Substring(0,slash);exact=false;}
            return false;
        }
        public void Set(string path, bool directory, bool include)
        {
            path=Canonical(path);
            Rules.RemoveAll(r=>string.Equals(r.Path,path,StringComparison.OrdinalIgnoreCase) || directory && r.Path.StartsWith(path+"\\",StringComparison.OrdinalIgnoreCase));
            Rules.Add(new SelectionRule {Path=path,Directory=directory,Include=include});
            index=null;identity=null;
        }
        public void Normalize()
        {
            if(Rules==null) Rules=new List<SelectionRule>();
            foreach(var rule in Rules) rule.Path=Canonical(rule.Path);
            index=null;identity=null;
        }
        public string Identity(){if(identity==null){var value=new System.Text.StringBuilder();foreach(var rule in Rules.OrderBy(r=>r.Path,StringComparer.OrdinalIgnoreCase).ThenBy(r=>r.Directory).ThenBy(r=>r.Include))value.Append(rule.Path.Length).Append(':').Append(rule.Path).Append(rule.Directory?'D':'F').Append(rule.Include?'1':'0');identity=value.ToString();}return identity;}
    }
    public sealed class FileBackupView
    {
        public string ArchivePath;
        public long Size;
        public long Modified;
        public long ArchiveId;
        public string ArchiveName;
        public string Status;
        public int TargetCopies;
        public int ValidCopies;
        public int UnavailableCopies,PendingCopies,FailedCopies;
        public string Locations;
        public static bool Same(FileBackupView a,FileBackupView b){return ReferenceEquals(a,b)||a!=null&&b!=null&&a.ArchivePath==b.ArchivePath&&a.Size==b.Size&&a.Modified==b.Modified&&a.ArchiveId==b.ArchiveId&&a.ArchiveName==b.ArchiveName&&a.Status==b.Status&&a.TargetCopies==b.TargetCopies&&a.ValidCopies==b.ValidCopies&&a.UnavailableCopies==b.UnavailableCopies&&a.PendingCopies==b.PendingCopies&&a.FailedCopies==b.FailedCopies&&a.Locations==b.Locations;}
    }
}
