namespace SunshineDuoSetup;

// 无头控制台 reporter：实现 IRunReporter，直接把日志写到 stdout（供 CLI 模式使用）。
internal sealed class ConsoleReporter : IRunReporter
{
    public void Log(string message) => Console.WriteLine(message);
    public void StepStart(string key) { }
    public void StepDone(string key, bool ok) { }
}