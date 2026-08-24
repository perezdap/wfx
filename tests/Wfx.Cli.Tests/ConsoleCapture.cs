namespace Wfx.Cli.Tests;

internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextReader _originalInput = Console.In;
    private readonly TextWriter _originalOutput = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly StringReader? _input;

    public ConsoleCapture(string? input = null, TextWriter? error = null)
    {
        Output = new StringWriter();
        Error = error ?? new StringWriter();
        _input = input is null ? null : new StringReader(input);
        if (_input is not null)
        {
            Console.SetIn(_input);
        }

        Console.SetOut(Output);
        Console.SetError(Error);
    }

    public StringWriter Output { get; }

    public TextWriter Error { get; }

    public string ErrorText => Error.ToString() ?? string.Empty;

    public void Dispose()
    {
        Console.SetIn(_originalInput);
        Console.SetOut(_originalOutput);
        Console.SetError(_originalError);
        _input?.Dispose();
        Output.Dispose();
        Error.Dispose();
    }
}
