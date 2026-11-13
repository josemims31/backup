using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private readonly Dictionary<string, FileSelection> selectionDrafts = new Dictionary<string, FileSelection>();
        private FileSelection SelectionDraft()
        {
            if (CurrentTask == null)
                return new FileSelection();
            FileSelection value;
            if (!selectionDrafts.TryGetValue(CurrentTask.Id, out value))
            {
                value = CopySelection(CurrentTask.Selection);
                selectionDrafts[CurrentTask.Id] = value;
            }

            return value;
        }

        private static FileSelection CopySelection(FileSelection value)
        {
            return JsonSettings.Decode(JsonSettings.Encode(value), new FileSelection());
        }

        private static string SelectionIdentity(FileSelection value)
        {
            return value.Identity();
        }

        public bool SelectionDirty
        {
            get
            {
                return CurrentTask != null && SelectionIdentity(Selection) != SelectionIdentity(CurrentTask.Selection);
            }
        }

        public void SaveSelection()
        {
            if (MutationBlocked || CurrentTask == null)
                return;
            var previous = CurrentTask.Selection;
            CurrentTask.Selection = CopySelection(Selection);
            try
            {
                SaveWorkspace();
            }
            catch (global::System.Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/SelectionEditing.cs:SaveSelection", diagnosticFailure);
                CurrentTask.Selection = previous;
                throw;
            }

            UpdateSelectionControls();
            Log("勾选已保存：" + CurrentTask.Name);
        }

        public void ResetSelection()
        {
            if (MutationBlocked || CurrentTask == null)
                return;
            selectionDrafts.Remove(CurrentTask.Id);
            ApplySelection();
            Log("勾选已重置为上次保存的状态：" + CurrentTask.Name);
        }

        private void UpdateSelectionControls()
        {
            bool dirty = SelectionDirty;
            Button("SaveSelection").IsEnabled = !MutationBlocked && SourceViewCurrent && dirty;
            Button("ResetSelection").IsEnabled = !MutationBlocked && SourceViewCurrent && dirty;
            Control<System.Windows.Controls.TreeView>("SourceTree").Tag = !MutationBlocked && SourceViewCurrent;
            Grid("FileGrid").IsReadOnly = MutationBlocked || !SourceViewCurrent;
            Control<CheckBox>("SelectAllTree").IsEnabled = !MutationBlocked && SourceViewCurrent;
            Button("StartBackup").IsEnabled = !MutationBlocked && SourceViewCurrent && CurrentTask != null && !dirty;
            Text("SelectionNotice").Text = dirty ? "勾选有未保存的更改，请保存或重置。" : "";
        }

        private void ShowSelectionSummary(int count, long bytes)
        {
            Text("SelectionSummary").Text = "已勾选" + count + "个文件";
            Text("SelectionSummary").ToolTip = "总大小：" + Display.Bytes(bytes) + " · 排除 " + Tree.ExcludedCount + " 项";
            UpdateSelectionControls();
            if (previewParent != null)
                ShowFileDetails(Grid("FileGrid").SelectedItem as FileTreeNode ?? previewParent);
        }
    }
}
