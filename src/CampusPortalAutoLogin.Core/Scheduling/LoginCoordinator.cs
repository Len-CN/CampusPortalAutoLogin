using CampusPortalAutoLogin.Core.Models;
namespace CampusPortalAutoLogin.Core.Scheduling;
public sealed class LoginCoordinator(ILoginFlow flow, ILogSink log, Func<TimeSpan, CancellationToken, Task>? delay = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;
    private volatile bool busy;
    public bool IsBusy => busy;
    public event Action<LoginProgress>? ProgressChanged;
    public event Action<LoginResult>? Completed;
    public async Task<LoginResult> TriggerAsync(string source, AppSettings settings, LoginRequest request, CancellationToken token = default)
    {
        if (lifetime.IsCancellationRequested || token.IsCancellationRequested) return new(LoginState.Cancelled, "操作已取消。");
        if (!await gate.WaitAsync(0, token).ConfigureAwait(false)) { log.Write(LogLevel.Info, source, "已有登录流程，触发已合并。"); return new(LoginState.Merged, "已有登录流程。"); }
        busy = true;
        LoginResult result;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            try { log.Clear(); }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            { log.Write(LogLevel.Warning, source, "覆盖上一轮日志失败：" + e.GetType().Name); }
            try
            {
                log.Write(LogLevel.Info, source, "开始探测与登录。");
                var progress = new DirectProgress(p => ProgressChanged?.Invoke(p));
                for (var attempt = 0; ; attempt++)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    result = await flow.ExecuteAsync(request, settings.RequestTimeoutSeconds, progress, linked.Token).ConfigureAwait(false);
                    if (!result.Retryable || attempt >= settings.MaxRetries) break;
                    var seconds = settings.RetryDelaysSeconds[Math.Min(attempt, settings.RetryDelaysSeconds.Length - 1)];
                    log.Write(LogLevel.Warning, source, $"第 {attempt + 1} 次重试将在 {seconds} 秒后开始。");
                    ProgressChanged?.Invoke(new(LoginState.Warning, $"暂时无法连接，{seconds} 秒后重试（{attempt + 1}/{settings.MaxRetries}）。"));
                    await wait(TimeSpan.FromSeconds(seconds), linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { result = new(LoginState.Cancelled, "操作已取消。"); }
            catch (Exception e) { result = new(LoginState.Failed, "登录流程异常：" + e.GetType().Name); }
            log.Write(result.IsSuccess ? LogLevel.Success : result.State == LoginState.Failed ? LogLevel.Error : LogLevel.Warning, source, result.Message);
        }
        finally { busy = false; gate.Release(); }
        Completed?.Invoke(result);
        return result;
    }
    public void Cancel() => lifetime.Cancel();
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await gate.WaitAsync().ConfigureAwait(false);
        gate.Release();
        // 不释放信号量，避免已经排队的取消触发与关闭并发访问已释放对象。
    }
    private sealed class DirectProgress(Action<LoginProgress> report) : IProgress<LoginProgress> { public void Report(LoginProgress value) => report(value); }
}
