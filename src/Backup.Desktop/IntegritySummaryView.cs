using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private bool historySummaryActive;
        private ISet<string> historySummaryScope;
        private DateTime historySummaryTime;
        private int historySummaryDirectoryErrors,historySummaryRepairErrors,historySummaryManagedFiles,historySummaryRegistrationIssues,historySummaryObsoleteArchives;
        private long historySummaryStamp=long.MinValue;
        private BackupIntegritySummary historySummaryCounts;
        private OperationState historySummaryState;
        private static ISet<string> ObservedRecoveryObjects(BackupCatalog catalog,AllBackupCheckResult review)
        {
            var result=new HashSet<string>(StringComparer.Ordinal);if(review==null)return result;
            foreach(var group in review.Findings.Where(item=>item.Status=="unregistered-copy"&&item.RepairAction=="register-copy"&&item.ExpectedSize>=0&&item.ActualSize==item.ExpectedSize).GroupBy(item=>item.Destination+"\n"+item.Folder)){
                var first=group.First();if(string.IsNullOrEmpty(first.Folder))continue;var record=catalog.GetInventoryFolder(first.Destination,first.Folder);if(record==null)continue;
                var entries=JsonSettings.Decode(record.Json,new List<StorageEntry>());
                foreach(var item in group)if(!string.IsNullOrEmpty(record.Error)||entries.Any(entry=>!entry.Directory&&entry.Name==item.Name&&entry.Size==item.ExpectedSize))result.Add(item.Name);
            }return result;
        }
        private void SetHistoryCheckText(string message,OperationState state){historySummaryState=state;Text("HistoryCheckSummary").Text=message;}
        private void PublishHistoryCheckSummary(IList<string> taskIds,int directoryErrors,int repairErrors=0)
        {
            var review=Backend.LastIntegrityReview;historySummaryManagedFiles=review==null?0:review.ManagedUnregistered;historySummaryRegistrationIssues=review==null?0:review.RegistrationAnomalies;historySummaryObsoleteArchives=review==null?0:review.ObsoleteIds.Count;
            historySummaryActive=true;historySummaryScope=taskIds==null?null:new HashSet<string>(taskIds);historySummaryTime=DateTime.Now;
            historySummaryDirectoryErrors=directoryErrors;historySummaryRepairErrors=repairErrors;historySummaryStamp=long.MinValue;RefreshHistoryCheckSummary();
        }
        private void RefreshHistoryCheckSummary()
        {
            if(!historySummaryActive)return;long stamp=Backend.Catalog.DataChangeStamp;
            if(historySummaryCounts==null||stamp!=historySummaryStamp){historySummaryCounts=Backend.Catalog.GetRecordedIntegritySummary(historySummaryScope,ObservedRecoveryObjects(Backend.Catalog,Backend.LastIntegrityReview));historySummaryStamp=stamp;}
            var counts=historySummaryCounts;string text=historySummaryTime.ToString("HH:mm:ss")+" · "+(historySummaryScope==null?"全部任务":"所选任务")+" · "+counts.Copies+" 个分卷副本，"+counts.RepairCopies+" 项待修复，"+counts.UnavailableCopies+" 项暂不可访问，"+historySummaryDirectoryErrors+" 个目录读取失败。";
            if(historySummaryObsoleteArchives>0)text+=" "+historySummaryObsoleteArchives+" 个旧版/失效包待清理。";if(historySummaryManagedFiles>0)text+=" "+historySummaryManagedFiles+" 个无登记备份文件。";if(historySummaryRegistrationIssues>0)text+=" "+historySummaryRegistrationIssues+" 项登记异常。";
            if(counts.PendingCopies>0)text+=" "+counts.PendingCopies+" 项待确认。";if(counts.IncompleteArchives>0)text+=" "+counts.IncompleteArchives+" 个压缩包副本不足。";
            if(historySummaryRepairErrors>0)text+=" 修复未完成："+historySummaryRepairErrors+" 项待重试。";
            historySummaryState=counts.RepairCopies>0||historySummaryRepairErrors>0||historySummaryManagedFiles>0||historySummaryRegistrationIssues>0||historySummaryObsoleteArchives>0?OperationState.Failed:counts.UnavailableCopies>0||counts.PendingCopies>0||counts.IncompleteArchives>0||historySummaryDirectoryErrors>0?OperationState.Attention:OperationState.Succeeded;
            Text("HistoryCheckSummary").Visibility=Visibility.Visible;Text("HistoryCheckSummary").Text=text;
        }
    }
}
