using System;
using System.ComponentModel;
using System.Windows.Forms;

namespace WindowsDriverBackup
{
    public partial class Form1 : Form
    {
        private bool operationRunning;

        public Form1()
        {
            InitializeComponent();
            Text += " v1.0.0";
        }

        private void BackupButtonClick(object sender, EventArgs e)
        {
            SelectFolderAndRun(true);
        }

        private void RestoreButtonClick(object sender, EventArgs e)
        {
            SelectFolderAndRun(false);
        }

        private void SelectFolderAndRun(bool backup)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = backup
                    ? "Select a destination for the driver backup."
                    : "Select the DriverBackup folder or the folder that contains it.";
                dialog.ShowNewFolderButton = backup;

                if (dialog.ShowDialog(this) == DialogResult.OK)
                    RunOperation(backup, dialog.SelectedPath);
            }
        }

        private void RunOperation(bool backup, string folder)
        {
            string operation = backup ? "Backup" : "Restore";
            BackgroundWorker worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.DoWork += delegate(object sender, DoWorkEventArgs e)
            {
                DriverBackupService service = new DriverBackupService();
                DriverBackupService.Progress progress = delegate(string message)
                {
                    worker.ReportProgress(0, message);
                };
                e.Result = backup ? service.Backup(folder, progress) : service.Restore(folder, progress);
            };
            worker.ProgressChanged += delegate(object sender, ProgressChangedEventArgs e)
            {
                statusLabel.Text = (string)e.UserState;
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                operationRunning = false;
                backupButton.Enabled = true;
                restoreButton.Enabled = true;
                worker.Dispose();

                if (e.Error != null)
                {
                    statusLabel.Text = operation + " failed.";
                    MessageBox.Show(this, e.Error.Message, operation, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                OperationResult result = (OperationResult)e.Result;
                statusLabel.Text = result.HasWarnings ? operation + " finished with warnings." : operation + " finished.";
                MessageBox.Show(this, result.GetMessage(), operation, MessageBoxButtons.OK,
                    result.HasWarnings ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            };

            operationRunning = true;
            backupButton.Enabled = false;
            restoreButton.Enabled = false;
            statusLabel.Text = operation + " in progress...";
            worker.RunWorkerAsync();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (operationRunning && e.CloseReason == CloseReason.UserClosing)
                e.Cancel = true;
        }
    }
}
