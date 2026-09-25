namespace Harmonia;

/// <summary>
/// Minimal logging seam used by the core layer so that it does not depend on
/// any particular logging implementation.
/// </summary>
public interface IHarmoniaLog
{
    void Debug(string message);

    void Info(string message);

    void Warning(string message, Exception? exception = null);

    void Error(string message, Exception? exception = null);
}
