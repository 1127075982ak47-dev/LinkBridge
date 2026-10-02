using PairedTlsProbe;

var mode = args.FirstOrDefault() ?? "help";
var options = args.Skip(1).ToArray();
switch (mode)
{
    case "input": await InputLoopbackProbe.RunAsync(options); break;
    case "audio": await AudioLoopbackProbe.RunAsync(options); break;
    case "settings": await SettingsPersistenceProbe.RunAsync(options); break;
    case "cross-input-server": await CrossInputServerProbe.RunAsync(options); break;
    case "cross-input-client": await CrossInputClientProbe.RunAsync(options); break;
    case "cross-audio-client": await CrossAudioClientProbe.RunAsync(options); break;
    case "cross-audio-server": await CrossAudioServerProbe.RunAsync(options); break;
    default:
        Console.WriteLine("Modes: input | audio | settings | cross-input-server | cross-input-client <ssh-alias> <remote-probe-dll> | cross-audio-client <forwarded-port> | cross-audio-server <ssh-alias> <remote-probe-dll>");
        break;
}
