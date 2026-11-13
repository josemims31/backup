using System;
using System.Globalization;

namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
#if UI_TEST
        public static Func<DateTime> ArchiveClock;
#endif
        private string NextArchiveNumber()
        {
            DateTime now = DateTime.Now;
#if UI_TEST
            if (ArchiveClock != null)
                now = ArchiveClock();
#endif
            string stamp = now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture), key = "archive_number:" + stamp;
            string previous = GetSetting(key), suffix = previous == null ? "" : IncrementArchiveSuffix(previous);
            database.Execute("INSERT OR REPLACE INTO settings(key,value) VALUES(?,?)", key, suffix);
            return stamp + (suffix.Length == 0 ? "" : "-" + suffix);
        }

        // Decimal text avoids imposing a digit limit on packages created in one second.
        private static string IncrementArchiveSuffix(string value)
        {
            if (value.Length == 0)
                return "1";
            char[] digits = value.ToCharArray();
            for (int index = digits.Length - 1; index >= 0; index--)
            {
                if (digits[index] != '9')
                {
                    digits[index]++;
                    return new string (digits);
                }

                digits[index] = '0';
            }

            return "1" + new string (digits);
        }
    }
}
