using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Backup.Core
{
    public enum RepositoryPathKind { FileSystem, Uri }
    public enum RepositoryAuthentication { None, Password, Ssh }
    public sealed class RepositoryModuleInfo
    {
        public string Protocol {get;private set;}
        public string DisplayName {get;private set;}
        public RepositoryPathKind PathKind {get;private set;}
        public RepositoryAuthentication Authentication {get;private set;}
        public IList<string> Schemes {get;private set;}
        public RepositoryModuleInfo(string protocol,string displayName,RepositoryPathKind pathKind,RepositoryAuthentication authentication,params string[] schemes)
        {Protocol=protocol;DisplayName=displayName;PathKind=pathKind;Authentication=authentication;Schemes=Array.AsReadOnly((schemes??new string[0]).ToArray());}
    }
    // SDK contract. Constructors and descriptor access must perform no repository I/O.
    public interface IRepositoryModule
    {
        int ApiVersion {get;}
        RepositoryModuleInfo Info {get;}
        IManagedBackupDestination Create(string id,string location,RemoteConnectionOptions options);
    }
    // The host retains this root session; directory adapters must not close it.
    public interface IRepositorySession : IManagedBackupDestination,IDisposable
    {
        string ConnectionState {get;}
        string ConnectionError {get;}
        event Action ConnectionStateChanged;
        IManagedBackupDestination ForDirectory(string location,CancellationToken cancellation);
        void ShareDirectoryRefresh(DirectoryRefreshCache cache,string credentialScope);
        IList<StorageEntry> ListEntriesAt(string location,CancellationToken cancellation,bool fresh);
    }
    public interface IOpenListRepositoryModule
    {
        string ServiceAddress(string location);
        bool Authorize(string location,RemoteConnectionOptions options,string otp,CancellationToken cancellation);
        void ValidateAuthorization(string location,RemoteConnectionOptions options);
    }
    public interface IOpenListRepositorySession
    {
        Func<string,RemoteConnectionOptions,bool> RenewOpenListAuthorization {get;set;}
        Func<bool> OpenListWebDavFallback {get;set;}
        event Action<string> UploadDiagnostic;
        void UseOpenListAuthorization(RemoteConnectionOptions options);
    }
    public static class OpenListAuthorization
    {
        private static IOpenListRepositoryModule Module {get{return RepositoryModules.Find("webdav") as IOpenListRepositoryModule;}}
        public static string OpenListServiceAddress(string location){var module=Module;return module==null?null:module.ServiceAddress(location);}
        public static bool AuthorizeOpenList(string location,RemoteConnectionOptions options,string otp=null,CancellationToken cancellation=default(CancellationToken))
        {var module=Module;if(module==null)throw new NotSupportedException("WebDAV 仓库模块不支持 OpenList 授权。");return module.Authorize(location,options,otp,cancellation);}
        public static void ValidateCachedOpenListAuthorization(string location,RemoteConnectionOptions options)
        {var module=Module;if(module!=null)module.ValidateAuthorization(location,options);}
    }
    public static class RepositoryModules
    {
        public const int CurrentApiVersion=1;
        public const string DirectoryName="repositories";
        private sealed class ModuleSet
        {
            internal readonly Dictionary<string,IRepositoryModule> Drivers=new Dictionary<string,IRepositoryModule>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<string> Errors=new List<string>();
        }
        private static readonly Lazy<ModuleSet> modules=new Lazy<ModuleSet>(Load,LazyThreadSafetyMode.ExecutionAndPublication);
        public static string ModuleDirectory {get{return Path.Combine(Path.GetDirectoryName(typeof(RepositoryModules).Assembly.Location),DirectoryName);}}
        public static IList<RepositoryModuleInfo> Available {get{return modules.Value.Drivers.Values.Select(module=>module.Info).OrderBy(info=>Order(info.Protocol)).ThenBy(info=>info.DisplayName,StringComparer.Ordinal).ToList().AsReadOnly();}}
        public static IList<string> Diagnostics {get{return modules.Value.Errors.AsReadOnly();}}
        private static int Order(string protocol){switch(protocol){case "local":return 0;case "smb":return 1;case "webdav":return 2;case "sftp":return 3;default:return 4;}}
        private static IEnumerable<string> ModuleFiles(ModuleSet result,string root)
        {
            string[] folders;
            try{folders=Directory.GetDirectories(root);}catch(Exception error){result.Errors.Add("仓库模块目录无法读取："+error.Message);yield break;}
            foreach(string folder in folders.OrderBy(path=>path,StringComparer.OrdinalIgnoreCase))
            {
                string[] files;
                try{files=Directory.GetFiles(folder,"Backup.Repository.*.dll",SearchOption.TopDirectoryOnly);}
                catch(Exception error){result.Errors.Add(Path.GetFileName(folder)+"：模块目录无法读取："+error.Message);continue;}
                foreach(string file in files.OrderBy(path=>path,StringComparer.OrdinalIgnoreCase))yield return file;
            }
        }
        private static string ModuleLabel(string file){return Path.GetFileName(Path.GetDirectoryName(file))+"/"+Path.GetFileName(file);}
        private static ModuleSet Load()
        {
            var result=new ModuleSet();string folder=ModuleDirectory;
            if(!Directory.Exists(folder)){result.Errors.Add("仓库模块目录不存在："+folder);return result;}
            // Discover each module's own directory once, one level below the
            // installation's repositories folder. Never inspect warehouse contents.
            foreach(string file in ModuleFiles(result,folder))
            {
                try
                {
                    var assembly=Assembly.LoadFrom(file);var candidates=new List<IRepositoryModule>();
                    foreach(var type in assembly.GetExportedTypes().Where(type=>!type.IsAbstract&&typeof(IRepositoryModule).IsAssignableFrom(type)))
                    {
                        var module=(IRepositoryModule)Activator.CreateInstance(type);var info=module.Info;
                        if(module.ApiVersion!=CurrentApiVersion||info==null||string.IsNullOrWhiteSpace(info.DisplayName)||string.IsNullOrEmpty(info.Protocol)||info.Protocol.Any(c=>!(c>='a'&&c<='z'||c>='0'&&c<='9'||c=='-'))||!Enum.IsDefined(typeof(RepositoryPathKind),info.PathKind)||!Enum.IsDefined(typeof(RepositoryAuthentication),info.Authentication)||info.PathKind==RepositoryPathKind.Uri&&(info.Schemes.Count==0||info.Schemes.Any(scheme=>!Uri.CheckSchemeName(scheme))))
                            throw new InvalidOperationException("仓库模块接口版本或描述无效。");
                        string reserved=info.Protocol=="local"?"Local":info.Protocol=="smb"?"Smb":info.Protocol=="webdav"?"WebDav":info.Protocol=="sftp"?"Sftp":null;
                        if(reserved!=null&&assembly.GetName().Name!="Backup.Repository."+reserved)throw new InvalidOperationException("内置仓库协议名称已保留，请为扩展模块使用独立协议名。");
                        candidates.Add(module);
                    }
                    if(candidates.Count==0)throw new InvalidOperationException("未找到仓库模块入口。");
                    if(candidates.GroupBy(module=>module.Info.Protocol,StringComparer.OrdinalIgnoreCase).Any(group=>group.Count()>1)||candidates.Any(module=>result.Drivers.ContainsKey(module.Info.Protocol)))throw new InvalidOperationException("仓库协议重复，未加载该模块。");
                    foreach(var module in candidates)result.Drivers.Add(module.Info.Protocol,module);
                }
                catch(Exception error){result.Errors.Add(ModuleLabel(file)+"："+(error.InnerException??error).Message);}
            }
            if(result.Drivers.Count==0)result.Errors.Add("未找到可用仓库模块，请将模块及其依赖放入 repositories 下各自的子文件夹。");
            return result;
        }
        public static IRepositoryModule Require(string protocol)
        {IRepositoryModule module;if(!modules.Value.Drivers.TryGetValue(protocol??"",out module))throw new NotSupportedException("未加载仓库模块："+protocol+"。请检查安装目录的 repositories 文件夹。"+(Diagnostics.Count==0?"":" "+string.Join("；",Diagnostics)));return module;}
        public static IRepositoryModule Find(string protocol)
        {IRepositoryModule module;return modules.Value.Drivers.TryGetValue(protocol??"",out module)?module:null;}
        public static bool IsFileSystem(string protocol)
        {if(protocol=="local"||protocol=="smb")return true;IRepositoryModule module;return modules.Value.Drivers.TryGetValue(protocol??"",out module)&&module.Info.PathKind==RepositoryPathKind.FileSystem;}
        public static string NormalizeLocation(string protocol,string location,bool requireModule=true)
        {
            IRepositoryModule module;bool found=modules.Value.Drivers.TryGetValue(protocol??"",out module);if(requireModule&&!found)module=Require(protocol);
            if(IsFileSystem(protocol)){if(!Path.IsPathRooted(location))throw new ArgumentException("仓库必须是完整路径。");return StoragePath.NormalizeRoot(location);}
            string[] schemes=found?module.Info.Schemes.ToArray():protocol=="webdav"?new[]{"http","https"}:protocol=="sftp"?new[]{"sftp"}:null;
            if(schemes==null)return location;Uri uri;if(!Uri.TryCreate(location,UriKind.Absolute,out uri)||!schemes.Contains(uri.Scheme,StringComparer.OrdinalIgnoreCase))throw new ArgumentException("仓库地址协议不正确。");
            return RemotePath.Root(location,uri.Scheme).AbsoluteUri;
        }
        public static IManagedBackupDestination Create(string id,string protocol,string location,RemoteConnectionOptions options)
        {
            var module=Require(protocol);var destination=module.Create(id,NormalizeLocation(protocol,location),options??new RemoteConnectionOptions());
            if(destination==null||destination.Id!=id||!(destination is IRepositorySession)){DestinationAdapterRegistry.DisposeAdapter(destination);throw new InvalidOperationException("仓库模块未提供有效的会话或仓库标识："+protocol);}
            return destination;
        }
        public static string ChildLocation(DestinationDefinition destination,string location,string name)
        {return IsFileSystem(destination.Protocol)?Path.Combine(location,RemotePath.Name(name)):RemotePath.Append(location,name);}
        public static void CheckDirectory(string protocol,string root,string directory)
        {
            if(IsFileSystem(protocol)){string basis=StoragePath.NormalizeRoot(root),next=StoragePath.NormalizeRoot(directory);if(!next.Equals(basis,StringComparison.OrdinalIgnoreCase)&&!next.StartsWith(basis.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw new IOException("仓库路径越界。");}
            else{var basis=new Uri(NormalizeLocation(protocol,root));var next=new Uri(NormalizeLocation(protocol,directory));if(next.Scheme!=basis.Scheme||next.Authority!=basis.Authority||!next.AbsolutePath.StartsWith(basis.AbsolutePath,StringComparison.Ordinal))throw new IOException("仓库地址越界。");}
        }
    }
}
