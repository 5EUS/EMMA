namespace EMMA.Cli;

public class LineEditor
{
    private string _buffer = "";
    private int _cursor = 0;

    public string ReadLine(CommandHistory history, string prompt = "> ")
    {
        _buffer = "";
        _cursor = 0;

        Console.Write(prompt);

        while (true)
        {
            var key = Console.ReadKey(true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return _buffer;

                case ConsoleKey.Backspace:
                    if (_cursor > 0)
                    {
                        _buffer = _buffer.Remove(_cursor - 1, 1);
                        _cursor--;
                    }
                    break;

                case ConsoleKey.UpArrow:
                    _buffer = history.Previous();
                    _cursor = _buffer.Length;
                    break;

                case ConsoleKey.DownArrow:
                    _buffer = history.Next();
                    _cursor = _buffer.Length;
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        _buffer = _buffer.Insert(_cursor, key.KeyChar.ToString());
                        _cursor++;
                    }
                    break;
            }

            Redraw(prompt);
        }
    }

    private void Redraw(string prompt)
    {
        Console.Write("\r" + prompt + _buffer + " ");
        Console.SetCursorPosition(prompt.Length + _cursor, Console.CursorTop);
    }
}