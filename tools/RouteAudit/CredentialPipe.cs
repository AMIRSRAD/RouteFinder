using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

static class CredentialPipe
{
    public static async Task SendAsync(string name, Func<(string Email, string Password)> read)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await pipe.WaitForConnectionAsync(deadline.Token);
        var (email, password) = read();
        using var writer = new StreamWriter(pipe);
        await writer.WriteLineAsync(email);
        await writer.WriteLineAsync(password);
        await writer.FlushAsync(deadline.Token);
    }

    public static async Task<(string Email, string Password)> ReceiveAsync(string name, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        await pipe.ConnectAsync(deadline.Token);
        using var reader = new StreamReader(pipe);
        var email = await reader.ReadLineAsync(deadline.Token);
        var password = await reader.ReadLineAsync(deadline.Token);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("The credential pipe closed before sign-in data arrived.");
        return (email, password);
    }
}
