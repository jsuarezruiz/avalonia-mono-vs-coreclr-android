namespace Bench.Cli;

/// <summary>Progress lines overwrite each other; everything else gets its own line.</summary>
internal sealed class ConsoleLog
{
    private bool _inProgress;

    public void Write(string message)
    {
        var progress = message.StartsWith("run ", StringComparison.Ordinal) || message.StartsWith("recording ", StringComparison.Ordinal)
                       || message.StartsWith("rendering ", StringComparison.Ordinal);
        if (progress && !Console.IsErrorRedirected)
        {
            Console.Error.Write($"\r{message,-78}");
            _inProgress = true;
            return;
        }

        if (_inProgress) { Console.Error.WriteLine(); _inProgress = false; }
        Console.Error.WriteLine(message);
    }

    public void Done()
    {
        if (_inProgress) { Console.Error.WriteLine(); _inProgress = false; }
    }
}
