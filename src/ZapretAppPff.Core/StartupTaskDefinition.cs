using System.Text;
using System.Xml.Linq;

namespace ZapretAppPff.Core;

public static class StartupTaskDefinition
{
    private static readonly XNamespace TaskSchema = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static void Write(string path, string sid, string executable, string workingDirectory)
    {
        var task = TaskSchema;
        var logon = new XElement(task + "LogonTrigger",
            new XElement(task + "UserId", sid), new XElement(task + "Delay", "PT15S"));
        var principal = new XElement(task + "Principal", new XAttribute("id", "Pilot"),
            new XElement(task + "UserId", sid), new XElement(task + "LogonType", "InteractiveToken"),
            new XElement(task + "RunLevel", "HighestAvailable"));
        var settings = new XElement(task + "Settings",
            new XElement(task + "MultipleInstancesPolicy", "IgnoreNew"),
            new XElement(task + "DisallowStartIfOnBatteries", "false"),
            new XElement(task + "StopIfGoingOnBatteries", "false"),
            new XElement(task + "StartWhenAvailable", "true"),
            new XElement(task + "ExecutionTimeLimit", "PT0S"));
        var action = new XElement(task + "Exec", new XElement(task + "Command", executable),
            new XElement(task + "Arguments", "--tray"), new XElement(task + "WorkingDirectory", workingDirectory));
        var definition = new XDocument(
            new XElement(task + "Task", new XAttribute("version", "1.3"),
                new XElement(task + "Triggers", logon), new XElement(task + "Principals", principal),
                settings, new XElement(task + "Actions", new XAttribute("Context", "Pilot"), action)));

        using var writer = new StreamWriter(path, append: false, Encoding.Unicode);
        definition.Save(writer);
    }
}
