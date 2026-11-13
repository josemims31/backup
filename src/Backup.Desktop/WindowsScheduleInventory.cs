using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml;

namespace Backup.Desktop
{
    public sealed class WindowsScheduleRecord
    {
        public string Path, Xml, RepositoryId, PlanId, Executable, Arguments, WorkingDirectory, DisplayName, ReadError, UserId;
        public bool Enabled, Related;
    }

    public static partial class WindowsIntegration
    {
#if UI_TEST
        public static Func<IList<WindowsScheduleRecord>> InventoryOverride;
        public static Action<string> DeleteSystemOverride;
#endif
        internal static XmlDocument ReadTaskXml(string xml)
        {
            var document = new XmlDocument
            {
                XmlResolver = null
            };
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                document.Load(reader);
            return document;
        }

        internal static string XmlText(XmlDocument document, string path)
        {
            var node = document.SelectSingleNode(path);
            return node == null ? "" : node.InnerText;
        }

        internal static bool IsCurrentExecutable(string value, string expected)
        {
            try
            {
                return string.Equals(System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables((value ?? "").Trim().Trim('"'))), System.IO.Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        internal static bool IsCurrentUser(WindowsScheduleRecord item)
        {
            return string.IsNullOrEmpty(item.UserId) || SameScheduleUser(item.UserId.Trim());
        }

        internal static bool IsOtherInstalledExecutable(string value, string expected)
        {
            if (IsCurrentExecutable(value, expected))
                return false;
            try
            {
                string path = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables((value ?? "").Trim('"')));
                if (path.StartsWith(@"\\"))
                    return true;
                return File.Exists(path) && File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), "data", "catalog.db"));
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        public static WindowsScheduleRecord ParseSystemSchedule(string path, string xml)
        {
            var item = new WindowsScheduleRecord
            {
                Path = path,
                Xml = xml,
                DisplayName = path
            };
            string name = path.Substring(path.LastIndexOf('\\') + 1);
            var match = Regex.Match(name, @"^(?:Backup|GroupBackup)-([0-9a-fA-F]{32})-([0-9a-fA-F]{32})$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                item.RepositoryId = match.Groups[1].Value;
                item.PlanId = match.Groups[2].Value;
                item.Related = true;
            }

            try
            {
                var doc = ReadTaskXml(xml);
                string exec = "/*[local-name()='Task']/*[local-name()='Actions']/*[local-name()='Exec']/";
                item.Executable = XmlText(doc, exec + "*[local-name()='Command']");
                item.Arguments = XmlText(doc, exec + "*[local-name()='Arguments']");
                item.WorkingDirectory = XmlText(doc, exec + "*[local-name()='WorkingDirectory']");
                item.UserId = XmlText(doc, "/*[local-name()='Task']/*[local-name()='Principals']/*[local-name()='Principal']/*[local-name()='UserId']");
                string description = XmlText(doc, "/*[local-name()='Task']/*[local-name()='RegistrationInfo']/*[local-name()='Description']");
                var argument = Regex.Match(item.Arguments ?? "", @"^\s*--run-schedule\s+([0-9a-fA-F]{32})\s*$");
                string binary = System.IO.Path.GetFileName((item.Executable ?? "").Trim('"'));
                if (argument.Success && (binary.Equals("Backup.exe", StringComparison.OrdinalIgnoreCase) || binary.Equals("GroupBackup.exe", StringComparison.OrdinalIgnoreCase)) &&
                    (description.StartsWith("Backup", StringComparison.OrdinalIgnoreCase) ||
                    description.StartsWith("GroupBackup", StringComparison.OrdinalIgnoreCase)))
                {
                    item.Related = true;
                    if (item.PlanId == null)
                        item.PlanId = argument.Groups[1].Value;
                }

                if (description.Length > 0)
                    item.DisplayName = description;
                bool? enabled = TaskBoolean(TaskElement(doc.DocumentElement, "Settings"), "Enabled", true);
                item.Enabled = enabled == true;
                if (!enabled.HasValue)
                    item.ReadError = "系统计划启用标记无效";
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/WindowsScheduleInventory.cs:ParseSystemSchedule", error);
                if (!(error is XmlException) && !(error is ArgumentException) && !(error is InvalidOperationException))
                    throw;
                item.ReadError = error.Message;
            }

            return item;
        }

        public static IList<WindowsScheduleRecord> ReadSystemSchedules()
        {
#if UI_TEST
            if (InventoryOverride != null)
                return InventoryOverride();
            if (RegisterOverride != null || DeleteOverride != null)
                return new List<WindowsScheduleRecord>();
#endif
            var result = new List<WindowsScheduleRecord>();
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            object root = null;
            try
            {
                service.Connect();
                root = service.GetFolder("\\");
                ReadTaskFolder(root, result);
                return result;
            }
            finally
            {
                ReleaseTaskCom(root);
                ReleaseTaskCom((object)service);
            }
        }

        private static void ReleaseTaskCom(object item)
        {
            if (item != null && Marshal.IsComObject(item))
                Marshal.ReleaseComObject(item);
        }

        private static void ReadTaskFolder(object folder, IList<WindowsScheduleRecord> result)
        {
            object tasks = null, folders = null;
            try
            {
                tasks = ((dynamic)folder).GetTasks(1);
                int count = ((dynamic)tasks).Count;
                for (int index = 1; index <= count; index++)
                {
                    object task = null;
                    try
                    {
                        task = ((dynamic)tasks).Item(index);
                        string path = (string)((dynamic)task).Path;
                        WindowsScheduleRecord record;
                        try
                        {
                            record = ParseSystemSchedule(path, (string)((dynamic)task).Xml);
                        }
                        catch (UnauthorizedAccessException error)
                        {
                            record = ParseSystemSchedule(path, "");
                            record.ReadError = error.Message;
                        }
                        catch (COMException error)
                        {
                            record = ParseSystemSchedule(path, "");
                            record.ReadError = error.Message;
                        }

                        if (record.Related)
                            result.Add(record);
                    }
                    finally
                    {
                        ReleaseTaskCom(task);
                    }
                }

                folders = ((dynamic)folder).GetFolders(0);
                count = ((dynamic)folders).Count;
                for (int index = 1; index <= count; index++)
                {
                    object child = null;
                    try
                    {
                        child = ((dynamic)folders).Item(index);
                        try
                        {
                            ReadTaskFolder(child, result);
                        }
                        catch (UnauthorizedAccessException diagnosticFailure)
                        {
                            global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/WindowsScheduleInventory.cs:ReadTaskFolder", diagnosticFailure);
                        }
                        catch (COMException error)
                        {
                            if (error.HResult != unchecked((int)0x80070005))
                                throw;
                        }
                    }
                    finally
                    {
                        ReleaseTaskCom(child);
                    }
                }
            }
            finally
            {
                ReleaseTaskCom(folders);
                ReleaseTaskCom(tasks);
            }
        }

        public static void DeleteSystemSchedule(WindowsScheduleRecord item)
        {
            if (item == null || !item.Related || string.IsNullOrEmpty(item.Path) || item.Path[0] != '\\')
                throw new InvalidOperationException("无法识别此 Windows 计划。");
#if UI_TEST
            if (DeleteSystemOverride != null)
            {
                DeleteSystemOverride(item.Path);
                return;
            }

            if (DeleteOverride != null)
            {
                DeleteOverride(item.Path.TrimStart('\\'));
                return;
            }

#endif
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            object folder = null, task = null;
            try
            {
                service.Connect();
                int slash = item.Path.LastIndexOf('\\');
                folder = service.GetFolder(slash == 0 ? "\\" : item.Path.Substring(0, slash));
                string name = item.Path.Substring(slash + 1);
                try
                {
                    task = ((dynamic)folder).GetTask(name);
                }
                catch (Exception error)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/WindowsScheduleInventory.cs:DeleteSystemSchedule", error);
                    if (error.HResult == unchecked((int)0x80070002))
                        return;
                    throw;
                }

                var current = ParseSystemSchedule((string)((dynamic)task).Path, (string)((dynamic)task).Xml);
                if (!current.Related || current.PlanId != item.PlanId || current.RepositoryId != item.RepositoryId || current.Xml != item.Xml)
                    throw new IOException("系统计划已发生变化，请重新检测后重试。");
                ((dynamic)folder).DeleteTask(name, 0);
            }
            finally
            {
                ReleaseTaskCom(task);
                ReleaseTaskCom(folder);
                ReleaseTaskCom((object)service);
            }
        }

        internal static string ScheduleMismatch(WindowsScheduleRecord item, BackupSchedule plan, string executable)
        {
            if (item.ReadError != null)
                return "系统计划内容无法读取";
            try
            {
                return ScheduleDefinitionMismatch(item, plan, executable);
            }
            catch (XmlException)
            {
                return "系统计划内容无法读取";
            }
            catch (InvalidOperationException)
            {
                return "系统计划包含重复或无效字段";
            }
        }
    }
}
