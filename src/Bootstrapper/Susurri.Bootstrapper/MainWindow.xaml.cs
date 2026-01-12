using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Microsoft.Extensions.Logging;
using Susurri.Modules.DHT.Core.Node;
using Susurri.Modules.IAM.Application.Commands;
using Susurri.Shared.Abstractions.Commands;

namespace Susurri.Bootstrapper;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly ICommandDispatcher _commandDispatcher;
    private readonly ILogger<MainWindow> _logger;
    
    public MainWindow(ICommandDispatcher commandDispatcher, ILogger<MainWindow> logger)
    {
        InitializeComponent();
        
        _commandDispatcher = commandDispatcher;
        _logger = logger;

        TestSignUp();
        TestDhtNode();
    }

    private async void TestSignUp()
    {
        var command = new Login("magiccactus42", "begin map mill could harsh man future win heart rapid woman race");
        await _commandDispatcher.SendAsync(command);
        _logger.LogInformation("sign up tested");
    }

    private async void TestDhtNode()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var logger = loggerFactory.CreateLogger<NodeServer>();

        var node = new NodeServer(7070, logger);
        var nodeTask = node.StartAsync(); 

        await Task.Delay(1000);

        var client = new NodeClient();
        bool alive = await client.PingAsync("127.0.0.1", 7070);
        Console.WriteLine(alive ? "Node is active" : "Node not responding");

        node.Stop();
        await nodeTask;

    }
}