using System.Text;
using System.Text.Json;
using RouteFinder.Core;

namespace RouteFinder.App;

internal static class SelfTest
{
    public static int Run(string output)
    {
        var results = new List<object>(); var failed = false;
        void Check(string name, Action action) { try { action(); results.Add(new { name, passed = true, error = (string?)null }); } catch (Exception e) { failed = true; results.Add(new { name, passed = false, error = e.Message }); } }
        Check("Windows account encryption roundtrip", () => { var plain = Encoding.UTF8.GetBytes("RouteFinder test data"); var encrypted = ProfileStore.Protect(plain, true); if (encrypted.SequenceEqual(plain) || !ProfileStore.Protect(encrypted, false).SequenceEqual(plain)) throw new Exception("Encryption mismatch"); });
        Check("Installed WireSock exports resolve without creating a tunnel", () => { using var sdk = new WireSockTunnel(); if (sdk.IsActive) throw new Exception("Unexpected active handle"); });
        Check("Classic preserves checked and unchecked executables", () =>
        {
            var directory = Directory.CreateTempSubdirectory("RouteFinder-classic-check-");
            ProfileStore.VerificationDirectory = directory.FullName;
            try
            {
                const string selected = @"C:\Games\selected.exe";
                const string uncheckedApp = @"C:\Games\unchecked.exe";
                ProfileStore.SaveApplications([selected, uncheckedApp]);
                ProfileStore.SavePreferences(new(false, [], UncheckedApplications: [uncheckedApp]));
                using var form = new MainForm();
                _ = form.Handle;
                try
                {
                    var list = Descendants(form).OfType<CheckedListBox>().Single();
                    if (list.Items.Count != 2 || !list.CheckedItems.Cast<string>().SequenceEqual([selected]))
                        throw new Exception("Classic did not restore the checked state.");
                    list.SetItemChecked(0, false);
                    Application.DoEvents();
                    if (!(ProfileStore.LoadApplications() ?? []).SequenceEqual([selected, uncheckedApp]) ||
                        !(ProfileStore.LoadPreferences().UncheckedApplications ?? []).SequenceEqual([selected, uncheckedApp]))
                        throw new Exception("Saving checkbox state lost an executable or its selection.");
                }
                finally { form.Close(); }
            }
            finally
            {
                ProfileStore.VerificationDirectory = null;
                foreach (var file in directory.EnumerateFiles()) file.Delete();
                directory.Delete();
            }
        });
        Check("Main window renders without networking", () =>
        {
            using var form = new MainForm(preview: true);
            form.Show(); Application.DoEvents();
            var path = Path.GetFullPath(output);
            void Capture(string suffix)
            {
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + suffix + ".png"));
            }
            var connect = Descendants(form).OfType<Button>().Single(button => button.Text == "Connect");
            if (!connect.Visible || !Descendants(form).OfType<Label>().Any(label => label.Visible && label.Text == "2 apps selected"))
                throw new Exception("The overview did not show the routing scope and persistent connection control.");
            Capture("");
            Descendants(form).OfType<Button>().Single(button => button.Text == "Routes").PerformClick();
            Application.DoEvents();
            var routes = Descendants(form).OfType<DataGridView>().Single();
            if (!connect.Visible || !routes.Visible || routes.Rows.Count != 6 || routes.CurrentRow?.Cells[2].Value as string != "42 ms")
                throw new Exception("The route workspace did not display its preview catalog and test result.");
            Capture("-routes");
            Descendants(form).OfType<Button>().Single(button => button.Text == "Applications").PerformClick();
            Application.DoEvents();
            if (!connect.Visible || !Descendants(form).OfType<CheckedListBox>().Any(list => list.Visible && list.CheckedItems.Count == 2))
                throw new Exception("The Applications page did not display its checked apps.");
            Capture("-apps");
            form.Close();
        });
        Check("Sign-in dialog renders with a masked password", () =>
        {
            using var dialog = new SignInDialog((_, _) => throw new Exception("Preview must not submit credentials."));
            dialog.Show(); Application.DoEvents();
            if (!Descendants(dialog).OfType<TextBox>().Any(input => input.UseSystemPasswordChar))
                throw new Exception("The password field is not masked.");
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
            var path = Path.GetFullPath(output);
            bitmap.Save(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-signin.png"));
            dialog.Close();
        });
        File.WriteAllText(Path.GetFullPath(output), JsonSerializer.Serialize(new { passed = !failed, networkChanged = false, results }, new JsonSerializerOptions { WriteIndented = true }));
        return failed ? 1 : 0;
    }

    static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
