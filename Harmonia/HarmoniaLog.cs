using System;
using Dalamud.Plugin.Services;

namespace Harmonia;

public sealed class HarmoniaLog : IHarmoniaLog
{
    private readonly IPluginLog log;

    public HarmoniaLog(IPluginLog log)
    {
        this.log = log;
    }

    public void Debug(string message) => log.Debug(message);

    public void Info(string message) => log.Information(message);

    public void Warning(string message, Exception? exception = null)
    {
        if (exception is null)
            log.Warning(message);
        else
            log.Warning(exception, message);
    }

    public void Error(string message, Exception? exception = null)
    {
        if (exception is null)
            log.Error(message);
        else
            log.Error(exception, message);
    }
}
