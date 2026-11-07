namespace RouteFinder.App;

internal sealed class SignInDialog : Form
{
    bool signingIn;

    public SignInDialog(Func<string, string, Task> signIn)
    {
        Text = UiLanguage.Text("Add EZConnect account");
        RightToLeft = UiLanguage.IsRightToLeft ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = UiLanguage.IsRightToLeft;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(770, 425);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        BackColor = StudioTheme.Base;
        Font = new Font("Segoe UI", 10);
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        var intro = StudioTheme.Stack(41, 83, 0, 42);
        intro.Padding = new Padding(23);
        intro.BackColor = StudioTheme.Sidebar;
        intro.Controls.Add(StudioTheme.Label("ROUTEFINDER", 11, true, StudioTheme.Accent), 0, 0);
        intro.Controls.Add(StudioTheme.Label("Route sources", 23, true), 0, 1);
        intro.Controls.Add(new NetworkArt { Margin = Padding.Empty }, 0, 2);
        intro.Controls.Add(StudioTheme.Label("Optional EZConnect account", 9, false, StudioTheme.Muted), 0, 3);
        split.Controls.Add(intro, 0, 0);

        var fields = StudioTheme.Stack(42, 45, 24, 38, 24, 38, 49, 44, 0);
        fields.Padding = new Padding(25, 20, 25, 12);
        fields.Controls.Add(StudioTheme.Label("Sign in to EZConnect", 23, true), 0, 0);
        fields.Controls.Add(StudioTheme.Label("Use your EZConnect account to load your route catalog.", 10, false, StudioTheme.Muted), 0, 1);
        var email = StudioTheme.Input("name@example.com"); email.Dock = DockStyle.Top; email.AccessibleName = "EZConnect email";
        var password = StudioTheme.Input("Enter your password"); password.Dock = DockStyle.Top;
        email.RightToLeft = password.RightToLeft = RightToLeft.No;
        password.UseSystemPasswordChar = true; password.AccessibleName = "EZConnect password";
        var remember = new CheckBox { Text = UiLanguage.Text("Remember me on this PC"), Checked = true, AutoSize = true, ForeColor = StudioTheme.Ink, BackColor = StudioTheme.Base, Margin = new Padding(0, 6, 0, 0) };
        fields.Controls.Add(StudioTheme.Label("Email address", 9, true), 0, 2);
        fields.Controls.Add(email, 0, 3);
        fields.Controls.Add(StudioTheme.Label("Password", 9, true), 0, 4);
        fields.Controls.Add(password, 0, 5);
        fields.Controls.Add(remember, 0, 6);
        var status = StudioTheme.Label("", 9, false, StudioTheme.Warning);
        status.AutoEllipsis = false;
        fields.Controls.Add(status, 0, 7);
        var actions = StudioTheme.Actions();
        var submit = StudioTheme.Button("Sign in", true);
        var cancel = StudioTheme.Button("Cancel"); cancel.DialogResult = DialogResult.Cancel;
        var recovery = StudioTheme.Button("Forgot password?");
        recovery.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://ezconnect.ir/users/forgot")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception error)
            {
                status.ForeColor = StudioTheme.Warning;
                status.Text = $"Could not open EZConnect password recovery. {error.Message}";
            }
        };
        actions.Controls.AddRange([submit, cancel, recovery]);
        fields.Controls.Add(actions, 0, 8);
        split.Controls.Add(fields, 1, 0);
        Controls.Add(split);
        AcceptButton = submit;
        CancelButton = cancel;
        Shown += (_, _) => email.Focus();
        FormClosing += (_, e) => { if (signingIn) e.Cancel = true; };
        Remember = () => remember.Checked;
        Email = () => email.Text.Trim();
        submit.Click += async (_, _) =>
        {
            if (!System.Net.Mail.MailAddress.TryCreate(email.Text.Trim(), out var address) || password.TextLength == 0)
            {
                status.Text = "Enter a valid email address and password.";
                return;
            }
            signingIn = true;
            submit.Enabled = cancel.Enabled = recovery.Enabled = email.Enabled = password.Enabled = false;
            status.ForeColor = StudioTheme.Muted;
            status.Text = "Signing in and loading routes…";
            try
            {
                await signIn(address.Address, password.Text);
                signingIn = false;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception error)
            {
                status.ForeColor = StudioTheme.Warning;
                status.Text = error.Message;
            }
            finally
            {
                signingIn = false;
                password.Clear();
                submit.Enabled = cancel.Enabled = recovery.Enabled = email.Enabled = password.Enabled = true;
            }
        };
    }

    public Func<bool> Remember { get; }
    public Func<string> Email { get; }
}
