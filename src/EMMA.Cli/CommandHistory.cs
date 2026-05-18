namespace EMMA.Cli;

public class CommandHistory
{
    private readonly List<string> _history = [];
    private int _index = -1;

    public void Add(string command)
    {
        if (!string.IsNullOrWhiteSpace(command))
        {
            _history.Add(command);
            _index = -1;
        }
    }

    public string Previous()
    {
        if (_history.Count == 0) return "";

        if (_index < _history.Count - 1)
            _index++;

        return _history[_history.Count - 1 - _index];
    }

    public string Next()
    {
        if (_index > 0)
        {
            _index--;
            return _history[_history.Count - 1 - _index];
        }

        _index = -1;
        return "";
    }
}