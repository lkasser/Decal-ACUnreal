using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Setup.Common
{
    /// <summary>
    /// The setups' wizard, in the classic manner - a white band at the top saying what this step
    /// is, the step beneath, and Back, Next and Cancel at the foot on the right - with four steps:
    /// Welcome, the folder, Installing, and Finish.
    /// </summary>
    /// <remarks>
    /// The steps are the same for both setups; what each says, which folder it offers, what it
    /// checks before installing and what it does at the end are the product's own, filled in by
    /// its setup's constructor and the members it overrides. The install runs off the window's
    /// thread, so the progress bar moves while it does.
    /// </remarks>
    public abstract class SetupWizard : Form
    {
        private enum Step
        {
            Welcome,
            Folder,
            Installing,
            Finish,
        }

        private readonly Panel _header = new Panel();
        private readonly PictureBox _icon = new PictureBox();
        private readonly Label _heading = new Label();
        private readonly Label _subheading = new Label();
        private readonly Button _back = new Button();
        private readonly Button _next = new Button();
        private readonly Button _cancel = new Button();

        private readonly Panel _welcomePage = new Panel();
        private readonly Label _welcomeText = new Label();
        private readonly Panel _folderPage = new Panel();
        private readonly Label _folderPrompt = new Label();
        private readonly TextBox _folder = new TextBox();
        private readonly Button _browse = new Button();
        private readonly Label _folderNote = new Label();
        private readonly Panel _installPage = new Panel();
        private readonly Label _installing = new Label();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly Label _current = new Label();
        private readonly Panel _finishPage = new Panel();
        private readonly Label _finishText = new Label();

        private readonly (string Heading, string Subheading)[] _headings = new (string, string)[4];
        private string _browseDescription = "Choose a folder";
        private int _folderOptionsAt = 44;
        private int _finishOptionsAt = 84;
        private Step _step;
        private bool _busy;
        private bool _leaving;

        protected SetupWizard(SetupProduct product, SetupLog log)
        {
            Product = product ?? throw new ArgumentNullException(nameof(product));
            Log = log ?? SetupLog.None;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = product.DisplayName + " Setup";
            Icon = LoadIcon(SystemInformation.IconSize);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = Dlu(0, 0, 317, 193).Size;

            // The band at the top.
            _header.BackColor = SystemColors.Window;
            Add(this, _header, Dlu(0, 0, 317, 37));
            _heading.Font = new Font(Font, FontStyle.Bold);
            _heading.UseMnemonic = false;
            Add(_header, _heading, Dlu(14, 7, 260, 10));
            _subheading.UseMnemonic = false;
            Add(_header, _subheading, Dlu(21, 18, 253, 18));
            _icon.SizeMode = PictureBoxSizeMode.Zoom;
            _icon.Image = LoadIcon(new Size(48, 48)).ToBitmap();
            Add(_header, _icon, Dlu(286, 8, 21, 21));
            AddRule(Dlu(0, 37, 317, 1));

            // The steps.
            BuildWelcome();
            BuildFolder();
            BuildInstalling();
            BuildFinish();

            // The foot.
            AddRule(Dlu(7, 165, 303, 1));
            SetUpButton(_back, "< &Back", Dlu(150, 172, 50, 14));
            SetUpButton(_next, "&Next >", Dlu(200, 172, 50, 14));
            SetUpButton(_cancel, "Cancel", Dlu(260, 172, 50, 14));
            _back.Click += (_, _) => GoBack();
            _next.Click += async (_, _) => await GoNextAsync();
            _cancel.Click += (_, _) => Close();
            AcceptButton = _next;
            CancelButton = _cancel;

            // Layout stays suspended until the product's setup has added its own boxes, so that
            // the window is scaled for the screen with everything in it: see EndSetUp.
        }

        protected SetupProduct Product { get; }

        protected SetupLog Log { get; }

        /// <summary>The folder as the player left it on the folder step.</summary>
        protected string Folder => _folder.Text.Trim().Trim('"');

        /// <summary>Whether the install was done; the setup exits with success only then.</summary>
        public bool Installed { get; private set; }

        /// <summary>What the install did, once it has been done.</summary>
        protected InstallResult Result { get; private set; }

        // ------------------------------------------------------------------- what the product says

        protected void SetWelcome(string heading, string subheading, string text)
        {
            _headings[(int)Step.Welcome] = (heading, subheading);
            _welcomeText.Text = text;
        }

        /// <summary>
        /// Called last in the product's constructor, once everything is added: lays the window
        /// out, scaled for the screen, and shows the first step. A control added after this would
        /// miss the scaling.
        /// </summary>
        protected void EndSetUp()
        {
            ResumeLayout(false);
            PerformLayout();
            GoTo(Step.Welcome);
        }

        protected void SetFolderStep(string heading, string subheading, string prompt, string folder, string browseDescription)
        {
            _headings[(int)Step.Folder] = (heading, subheading);
            _folderPrompt.Text = prompt;
            _folder.Text = folder ?? string.Empty;
            _browseDescription = browseDescription ?? _browseDescription;
        }

        /// <summary>A line or two under the folder step's options.</summary>
        protected void SetFolderNote(string text) => _folderNote.Text = text ?? string.Empty;

        /// <summary>A box on the folder step, under the folder, one beneath another.</summary>
        protected CheckBox AddFolderOption(string text, bool on)
        {
            CheckBox box = new CheckBox { Text = text, Checked = on, UseVisualStyleBackColor = true };
            Add(_folderPage, box, Dlu(0, _folderOptionsAt, 289, 11));
            _folderOptionsAt += 13;
            return box;
        }

        protected void SetInstallingStep(string heading, string subheading, string text)
        {
            _headings[(int)Step.Installing] = (heading, subheading);
            _installing.Text = text;
        }

        protected void SetFinishStep(string heading, string subheading) => _headings[(int)Step.Finish] = (heading, subheading);

        /// <summary>A box on the last step, under what the install did.</summary>
        protected CheckBox AddFinishOption(string text, bool on)
        {
            // The text above gives up the foot of the step to the boxes.
            _finishText.Height = Dlu(0, 0, 289, 80).Height;

            CheckBox box = new CheckBox { Text = text, Checked = on, UseVisualStyleBackColor = true };
            Add(_finishPage, box, Dlu(0, _finishOptionsAt, 289, 11));
            _finishOptionsAt += 13;
            return box;
        }

        // ------------------------------------------------------------------- what the product does

        /// <summary>
        /// Checks the folder before installing into it, asking the player whatever needs asking.
        /// False keeps the wizard on the folder step. On the window's thread.
        /// </summary>
        protected abstract bool ReadyToInstall(string folder);

        /// <summary>Installs. Off the window's thread; a <see cref="SetupException"/> is shown and the folder step comes back.</summary>
        protected abstract InstallResult Install(string folder, IProgress<SetupProgress> progress);

        /// <summary>What the last step says the install did.</summary>
        protected abstract string DescribeResult(InstallResult result);

        /// <summary>Done when Finish is pressed: start the Agent, say.</summary>
        protected virtual void Finished()
        {
        }

        // ------------------------------------------------------------------- talking to the player

        protected void Tell(string text, MessageBoxIcon icon = MessageBoxIcon.Information)
            => MessageBox.Show(this, text, Text, MessageBoxButtons.OK, icon);

        protected bool Ask(string question, MessageBoxIcon icon = MessageBoxIcon.Question)
            => MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, icon, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

        /// <summary>Asks the player to do something and press Retry; false when they cancel instead.</summary>
        protected bool AskToRetry(string text)
            => MessageBox.Show(this, text, Text, MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) == DialogResult.Retry;

        // ------------------------------------------------------------------- the steps

        private void BuildWelcome()
        {
            _welcomeText.UseMnemonic = false;
            Add(_welcomePage, _welcomeText, Dlu(0, 0, 289, 116));
            Add(this, _welcomePage, Content);
        }

        private void BuildFolder()
        {
            _folderPrompt.UseMnemonic = false;
            Add(_folderPage, _folderPrompt, Dlu(0, 0, 289, 20));
            Add(_folderPage, _folder, Dlu(0, 24, 233, 13));
            _browse.Text = "B&rowse...";
            _browse.UseVisualStyleBackColor = true;
            _browse.Click += (_, _) => BrowseFolder();
            Add(_folderPage, _browse, Dlu(239, 23, 50, 14));
            _folderNote.UseMnemonic = false;
            _folderNote.ForeColor = SystemColors.GrayText;
            Add(_folderPage, _folderNote, Dlu(0, 76, 289, 40));
            Add(this, _folderPage, Content);
        }

        private void BuildInstalling()
        {
            _installing.UseMnemonic = false;
            Add(_installPage, _installing, Dlu(0, 8, 289, 10));
            Add(_installPage, _progress, Dlu(0, 22, 289, 11));
            _current.UseMnemonic = false;
            _current.AutoEllipsis = true;
            _current.ForeColor = SystemColors.GrayText;
            Add(_installPage, _current, Dlu(0, 38, 289, 10));
            Add(this, _installPage, Content);
        }

        private void BuildFinish()
        {
            _finishText.UseMnemonic = false;
            Add(_finishPage, _finishText, Dlu(0, 0, 289, 116));
            Add(this, _finishPage, Content);
        }

        private void GoTo(Step step)
        {
            _step = step;
            _welcomePage.Visible = step == Step.Welcome;
            _folderPage.Visible = step == Step.Folder;
            _installPage.Visible = step == Step.Installing;
            _finishPage.Visible = step == Step.Finish;

            (string heading, string subheading) = _headings[(int)step];
            _heading.Text = heading ?? string.Empty;
            _subheading.Text = subheading ?? string.Empty;

            _back.Visible = step == Step.Welcome || step == Step.Folder;
            _back.Enabled = step == Step.Folder;
            _next.Text = step == Step.Finish ? "&Finish" : step == Step.Folder ? "&Install" : "&Next >";
            _next.Enabled = step != Step.Installing;
            _cancel.Enabled = step != Step.Installing && step != Step.Finish;

            if (step == Step.Folder)
                _folder.Focus();
            else if (_next.Enabled)
                _next.Focus();
        }

        private void GoBack()
        {
            if (_step == Step.Folder)
                GoTo(Step.Welcome);
        }

        private async Task GoNextAsync()
        {
            switch (_step)
            {
                case Step.Welcome:
                    GoTo(Step.Folder);
                    break;

                case Step.Folder:
                    await InstallAsync();
                    break;

                case Step.Finish:
                    try
                    {
                        Finished();
                    }
                    finally
                    {
                        _leaving = true;
                        DialogResult = DialogResult.OK;
                        Close();
                    }

                    break;
            }
        }

        private async Task InstallAsync()
        {
            string folder = Folder;
            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
            {
                Tell("Choose a folder: a full path, such as " + AgentFolder.DefaultFolder + ".", MessageBoxIcon.Warning);
                return;
            }

            folder = Paths.Normalize(folder);
            _folder.Text = folder;
            if (!ReadyToInstall(folder))
                return;

            GoTo(Step.Installing);
            _busy = true;
            _progress.Value = 0;
            Progress<SetupProgress> progress = new Progress<SetupProgress>(ShowProgress);

            try
            {
                Log.Info($"Installing {Product.DisplayName} into {folder}.");
                Result = await Task.Run(() => Install(folder, progress));
                Installed = true;
                Log.Info($"Installed {Result.Installed} files into {Result.ProductFolder}.");
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the player is told and can choose another folder or quit;
                // a setup that vanished mid-install would leave them guessing.
                Log.Error(ex.Message);
                _busy = false;
                GoTo(Step.Folder);
                Tell(ex.Message, MessageBoxIcon.Error);
                return;
            }
            finally
            {
                _busy = false;
            }

            _finishText.Text = DescribeResult(Result);
            GoTo(Step.Finish);
        }

        private void ShowProgress(SetupProgress progress)
        {
            _progress.Maximum = Math.Max(1, progress.Total);
            _progress.Value = Math.Min(_progress.Maximum, progress.Done);
            _current.Text = progress.Current ?? string.Empty;
        }

        private void BrowseFolder()
        {
            using FolderBrowserDialog dialog = new FolderBrowserDialog { Description = _browseDescription, UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (Directory.Exists(Folder))
                dialog.InitialDirectory = Folder;
            if (dialog.ShowDialog(this) == DialogResult.OK)
                _folder.Text = dialog.SelectedPath;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Not while files are being written: a half-written install helps nobody.
            if (_busy)
            {
                e.Cancel = true;
                return;
            }

            if (!_leaving && !Installed && e.CloseReason == CloseReason.UserClosing
                && !Ask($"{Product.DisplayName} is not installed yet. Quit setup?"))
            {
                e.Cancel = true;
                return;
            }

            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            // Made here, so disposed here, once the controls showing them are gone.
            Image picture = disposing ? _icon.Image : null;
            Font bold = disposing ? _heading.Font : null;
            base.Dispose(disposing);
            picture?.Dispose();
            bold?.Dispose();
        }

        // ------------------------------------------------------------------- layout

        /// <summary>Where every step goes, between the band at the top and the buttons at the foot.</summary>
        private static Rectangle Content => Dlu(14, 44, 289, 116);

        /// <summary>
        /// A rectangle in dialog units, as Decal Agent's own dialogs are laid out: six pixels to
        /// four units across, thirteen to eight down, at 96 dots to the inch.
        /// </summary>
        protected static Rectangle Dlu(int x, int y, int width, int height)
            => Rectangle.FromLTRB(
                (int)Math.Round(x * 1.5),
                (int)Math.Round(y * 13 / 8.0),
                (int)Math.Round((x + width) * 1.5),
                (int)Math.Round((y + height) * 13 / 8.0));

        private static void Add(Control parent, Control control, Rectangle bounds)
        {
            control.Bounds = bounds;
            parent.Controls.Add(control);
        }

        private void SetUpButton(Button button, string text, Rectangle bounds)
        {
            button.Text = text;
            button.UseVisualStyleBackColor = true;
            Add(this, button, bounds);
        }

        /// <summary>A thin line across the window, between the band, the step and the buttons.</summary>
        private void AddRule(Rectangle at)
            => Add(this, new Label { AutoSize = false, BackColor = SystemColors.ControlDark }, new Rectangle(at.X, at.Y, at.Width, 1));

        /// <summary>Decal's icon, at the size asked for.</summary>
        public static Icon LoadIcon(Size size)
        {
            using Stream stream = typeof(SetupWizard).Assembly.GetManifestResourceStream("Setup.Common.Decal.ico");
            return stream == null ? SystemIcons.Application : new Icon(stream, size);
        }
    }
}
