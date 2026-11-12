using System;
namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        public void SaveScheduleConfiguration(string json)
        {
            Transaction(delegate{
                if(!SaveSettingUnsafe("backup_schedules_v1",json))return 0;
                database.Execute("UPDATE recovery_revision SET value=value+1 WHERE id=1");return 0;
            });
        }
    }
}
