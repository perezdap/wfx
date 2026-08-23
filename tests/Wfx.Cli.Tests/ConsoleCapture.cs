namespace Wfx.Cli.Tests;

internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextReader _originalInput = Console.In;
    private readonly TextWriter _originalOutput = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly StringReader? _input;
    private readonly TextWriter _error;

    public ConsoleCapture(string? input = null, TextWriter? error = null)
    {
        Output = new StringWriter();
        _error = error ?? new StringWriter();
        _input = input is null ? null : new StringReader(input);
        if (_input is not null)
        {
            Console.SetIn(_input);
        }

        Console.SetOut(Output);
        Console.SetError(_error);
    }

    public StringWriter Output { get; }

    public string ErrorText => _error.ToString() ?? string.Empty;

    public void Dispose()
    {
        Console.SetIn(_originalInput);
        Console.SetOut(_originalOutput);
        Console.SetError(_originalError);
        _input?.Dispose();
        Output.Dispose();
        _error.Dispose();
    }
}
