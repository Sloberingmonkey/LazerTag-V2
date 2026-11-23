namespace LazerTagUpdater;

internal sealed class UpdateForm : Form
{
    private readonly TextBox host = new() { Text = "lazertag-pi.local", AccessibleName = "Pi address" };
    private readonly TextBox user = new() { Text = "arenaadmin", AccessibleName = "SSH username" };
    private readonly TextBox password = new() { UseSystemPasswordChar = true, AccessibleName = "Pi account password" };
    private readonly CheckBox useKey = new() { Text = "Use an SSH private key", AutoSize = true };
    private readonly TextBox key = new() { AccessibleName = "SSH private key" };
    private readonly TextBox archive = new() { ReadOnly = true, AccessibleName = "Release archive" };
    private readonly Button install = new() { Text = "Install update", AutoSize = true, Padding = new(12, 3, 12, 3) };
    private readonly Button cancel = new() { Text = "Cancel", AutoSize = true, Enabled = false, Padding = new(8, 3, 8, 3) };
    private readonly ProgressBar bar = new() { Dock = DockStyle.Top, Height = 13 };
    private readonly RichTextBox status = new()
    {
        ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, DetectUrls = false,
        Dock = DockStyle.Top, Height = 74, ScrollBars = RichTextBoxScrollBars.Vertical, AccessibleName = "Update status"
    };
    private readonly TableLayoutPanel fields = Stack();
    private readonly SavedPiLogin? savedLogin = SavedPiLogin.Load(AppContext.BaseDirectory);
    private CancellationTokenSource? pending;
    private bool installing;

    private string SavedPassword => savedLogin?.PasswordFor(host.Text, user.Text) ?? "";

    public UpdateForm()
    {
        Text = "LazerTag Pi Updater";
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new(620, 510);
        MinimumSize = new(510, 490);
        var content = Stack();
        content.Dock = DockStyle.Fill;
        content.AutoScroll = true;
        content.Padding = new(22, 14, 22, 16);
        Controls.Add(content);

        var title = Caption("Update arena server");
        title.Font = new Font(Font.FontFamily, 18, FontStyle.Bold);
        content.Controls.Add(title);
        content.Controls.Add(Caption("Connect this PC to the Pi's management LAN. Installing restarts the arena service."));
        content.Controls.Add(fields);
        var login = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty };
        login.ColumnStyles.Add(new(SizeType.Percent, 60));
        login.ColumnStyles.Add(new(SizeType.Percent, 40));
        login.Controls.Add(Field("Pi address", host), 0, 0);
        login.Controls.Add(Field("SSH username", user), 1, 0);
        fields.Controls.Add(login);
        fields.Controls.Add(Field("Pi account password", password));
        void ShowSavedLogin() => password.PlaceholderText = SavedPassword.Length > 0 ? "Saved password for this Pi" : "";
        host.TextChanged += (_, _) => ShowSavedLogin();
        user.TextChanged += (_, _) => ShowSavedLogin();
        ShowSavedLogin();
        fields.Controls.Add(useKey);
        var keyRow = Picker(key, "SSH private key", "All files (*.*)|*.*");
        keyRow.Visible = false;
        fields.Controls.Add(keyRow);
        useKey.CheckedChanged += (_, _) => keyRow.Visible = useKey.Checked;
        fields.Controls.Add(Field("Server release (.tar.gz)", Picker(archive, "Choose the Linux ARM64 release", "Server release (*.tar.gz)|*.tar.gz")));
        string adjacent = Path.Combine(AppContext.BaseDirectory, "lazertag-linux-arm64.tar.gz");
        if (File.Exists(adjacent)) archive.Text = adjacent;

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new(0, 12, 0, 10) };
        install.BackColor = Color.FromArgb(24, 78, 111);
        install.ForeColor = Color.White;
        install.FlatStyle = FlatStyle.Flat;
        actions.Controls.Add(install);
        actions.Controls.Add(cancel);
        content.Controls.Add(actions);
        content.Controls.Add(bar);
        content.Controls.Add(status);
        content.Resize += (_, _) =>
        {
            int width = Math.Max(300, content.ClientSize.Width - content.Padding.Horizontal - 12);
            content.Controls[1].MaximumSize = new(width, 0);
        };
        status.Text = archive.Text.Length > 0 ? "Ready to install the selected release." : "Choose a server release to begin.";
        archive.TextChanged += (_, _) => status.Text = "Ready to install the selected release.";
        install.Click += async (_, _) => await StartUpdate();
        cancel.Click += (_, _) => pending?.Cancel();
        AcceptButton = install;
        FormClosing += (_, e) =>
        {
            if (pending == null || e.CloseReason == CloseReason.WindowsShutDown) return;
            e.Cancel = true;
            if (!installing) pending.Cancel();
        };
    }

    private static TableLayoutPanel Stack() => new()
    {
        ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top, Margin = Padding.Empty, GrowStyle = TableLayoutPanelGrowStyle.AddRows
    };

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new(3, 6, 3, 3) };

    private static Control Field(string caption, Control control)
    {
        var group = Stack();
        group.Controls.Add(Caption(caption));
        control.Dock = DockStyle.Top;
        group.Controls.Add(control);
        return group;
    }

    private Control Picker(TextBox box, string title, string filter)
    {
        var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top, Margin = Padding.Empty };
        row.ColumnStyles.Add(new(SizeType.Percent, 100));
        row.ColumnStyles.Add(new(SizeType.AutoSize));
        box.Dock = DockStyle.Top;
        row.Controls.Add(box, 0, 0);
        var browse = new Button { Text = "Browse…", AutoSize = true, AccessibleName = title };
        row.Controls.Add(browse, 1, 0);
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, RestoreDirectory = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName;
        };
        return row;
    }

    private async Task StartUpdate()
    {
        string address = host.Text.Trim(), username = user.Text.Trim();
        string secret = password.Text.Length == 0 ? SavedPassword : password.Text;
        string keyPath = useKey.Checked ? key.Text.Trim() : "", file = archive.Text;
        if (Uri.CheckHostName(address) == UriHostNameType.Unknown || username.Length == 0
            || (useKey.Checked ? !File.Exists(keyPath) : secret.Length == 0) || !File.Exists(file))
        {
            status.Text = "Enter the Pi address and SSH login, then choose an existing release file.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        pending = cancellation;
        fields.Enabled = install.Enabled = false;
        cancel.Enabled = true;
        bar.Style = ProgressBarStyle.Marquee;
        var progress = new Progress<UpdateProgress>(p =>
        {
            if (pending != cancellation) return;
            installing = p.Installing;
            cancel.Enabled = !installing;
            bar.Style = p.Percent.HasValue ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            bar.Value = p.Percent ?? 0;
            status.Text = p.Message;
        });
        var knownHosts = new TrustedHosts(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LazerTagUpdater", "hosts"));
        try
        {
            bool Trust(string algorithm, string fingerprint) => (bool)Invoke(() =>
                MessageBox.Show(this, $"First connection to {address}.\n\n{algorithm}\n{fingerprint}\n\nSave this fingerprint for this Pi?",
                    "Verify Pi identity", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes);
            string Passphrase() => (string)Invoke(() => AskPassphrase());
            await Task.Run(() => PiUpdate.InstallAsync(address, 22, username, secret, keyPath, file, knownHosts,
                Trust, Passphrase, progress, cancellation.Token));
            bar.Value = 100;
            status.Text = "Update installed. The arena server passed its health check.";
        }
        catch (OperationCanceledException) { bar.Value = 0; status.Text = "Update cancelled."; }
        catch (Exception ex)
        {
            bar.Value = 0;
            status.Text = ex.Message;
        }
        finally
        {
            password.Clear();
            pending = null;
            installing = false;
            fields.Enabled = install.Enabled = true;
            cancel.Enabled = false;
            bar.Style = ProgressBarStyle.Continuous;
        }
    }

    private string AskPassphrase()
    {
        using var dialog = new Form { Text = "Unlock SSH key", Font = Font, ClientSize = new(380, 125),
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
        var panel = Stack();
        panel.Padding = new(12);
        var entry = new TextBox { UseSystemPasswordChar = true };
        panel.Controls.Add(Field("SSH key passphrase", entry));
        var unlock = new Button { Text = "Unlock", DialogResult = DialogResult.OK, AutoSize = true };
        panel.Controls.Add(unlock);
        dialog.Controls.Add(panel);
        dialog.AcceptButton = unlock;
        return dialog.ShowDialog(this) == DialogResult.OK ? entry.Text : throw new OperationCanceledException();
    }
}
