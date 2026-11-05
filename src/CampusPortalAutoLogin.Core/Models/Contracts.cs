namespace CampusPortalAutoLogin.Core.Models;

public enum LoginState { Idle, Probing, LoggingIn, Success, Online, Warning, Failed, Offline, Cancelled, Merged, CaptchaRequired }
public sealed record LoginResult(LoginState State, string Message, bool Retryable = false, Uri? PortalUri = null)
{
    public bool IsSuccess => State is LoginState.Success or LoginState.Online;
}
public sealed record LoginRequest(string Username, string Password, string ServiceProvider);
public sealed record LoginProgress(LoginState State, string Message);
public interface ILoginFlow
{
    Task<LoginResult> ExecuteAsync(LoginRequest request, int timeoutSeconds, IProgress<LoginProgress>? progress, CancellationToken cancellationToken);
}
public enum LogLevel { Info, Success, Warning, Error }
public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message)
{
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss} [{Level}] [{Source}] {Message}";
}
public interface ILogSink
{
    void Write(LogLevel level, string source, string message);
    void Clear();
}
