using SCFleetCommand.Core.Events;

namespace SCFleetCommand.Core.Parsing;

public interface ILogEventParser
{
    FleetEvent? TryParse(string line);
}
