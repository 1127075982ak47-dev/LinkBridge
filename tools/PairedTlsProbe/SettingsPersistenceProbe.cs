#pragma warning disable CA1416 // Windows ACL branches are guarded at runtime; lambda analyzer does not infer the enclosing branch.
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using LinkBridge.Core;

namespace PairedTlsProbe;

internal static class SettingsPersistenceProbe
{
    internal static async Task RunAsync(string[] args)
    {
        void Assert(bool value,string name){if(!value)throw new Exception(name);}
        var directory=Path.Combine(Path.GetTempPath(),"LinkBridge-SettingsProbe-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var path=Path.Combine(directory,"settings.json");
        var save=typeof(SettingsStore).GetMethod("TrySaveToPath",BindingFlags.Static|BindingFlags.NonPublic)!;var load=typeof(SettingsStore).GetMethod("LoadFromPath",BindingFlags.Static|BindingFlags.NonPublic)!;
        bool Save(AppSettings settings,string destination){var args=new object?[]{settings,destination,null};var result=(bool)save.Invoke(null,args)!;if(!result)Console.WriteLine("SAVE FAILURE: "+args[2]);return result;}
        AppSettings Load()=> (AppSettings)load.Invoke(null,new object[]{path})!;
        var secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(16));var settings=new AppSettings{InputPairingSecret=secret,DisplayName="Atomic probe"};
        try
        {
         Assert(Save(settings,path),"initial atomic private save");
         if(OperatingSystem.IsWindows())
         {
          var allowed=new HashSet<string>{WindowsIdentity.GetCurrent().User!.Value,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null).Value,new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null).Value};
          foreach(var security in new FileSystemSecurity[]{new FileInfo(path).GetAccessControl(),new DirectoryInfo(directory).GetAccessControl()})
          {Assert(security.AreAccessRulesProtected,"ACL inheritance disabled");var rules=security.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();Assert(rules.All(rule=>!rule.IsInherited&&rule.AccessControlType==AccessControlType.Allow)&&rules.Select(rule=>rule.IdentityReference.Value).ToHashSet().SetEquals(allowed),"only current user/SYSTEM/Administrators allowed");}
          Console.WriteLine("PASS Windows real folder/file ACL: protected, 3 explicit allowed principals, no inherited access.");
         }
         else
         {
          Assert(File.GetUnixFileMode(directory)==(UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute),"own folder700");Assert(File.GetUnixFileMode(path)==(UnixFileMode.UserRead|UnixFileMode.UserWrite),"settings600");Console.WriteLine("PASS Unix real permissions: folder700/file600.");
         }
         var content=JsonSerializer.Serialize(new{displayName=new string('x',81),mediaPort=0,frameRate="wrong type",videoBitrateMbps=999,audioBitrateKbps=-1,remoteDisplayWidth=0,remoteDisplayHeight=50000,theme="unsupported",inputPairingSecret=secret,futureFeature=new{enabled=true,blob="opaque"}});
         File.WriteAllText(path,content);var before=File.ReadAllBytes(path);var normalized=Load();
         Assert(normalized.MediaPort==45679&&normalized.FrameRate==30&&normalized.VideoBitrateMbps==4&&normalized.AudioBitrateKbps==128&&normalized.RemoteDisplayWidth==2560&&normalized.RemoteDisplayHeight==1440&&normalized.Theme=="system"&&normalized.DisplayName.Length<=80,"invalid fields safely normalize");
         Assert(normalized.InputPairingSecret==secret,"load never rotates pairing secret");Assert(before.SequenceEqual(File.ReadAllBytes(path)),"load never rewrites invalid original JSON");
         Assert(Save(normalized,path),"safe defaults explicit save");using(var document=JsonDocument.Parse(File.ReadAllBytes(path)))Assert(document.RootElement.GetProperty("futureFeature").GetProperty("blob").GetString()=="opaque","unknown fields preserved on loaded settings save");
         Assert(Save(new AppSettings{DisplayName="Fresh model",InputPairingSecret=secret},path),"fresh model save preserves unknown disk fields");using(var document=JsonDocument.Parse(File.ReadAllBytes(path)))Assert(document.RootElement.TryGetProperty("futureFeature",out _),"unknown preserved from disk for fresh model");
         Console.WriteLine("PASS invalid types/ranges safely normalize in memory; secret unchanged; original not rewritten; unknown JSON preserved.");
         using var readerStop=new CancellationTokenSource();var observations=0;var reader=Task.Run(()=>{while(!readerStop.IsCancellationRequested){try{using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var document=JsonDocument.Parse(stream);Assert(document.RootElement.GetProperty("displayName").GetString() is not null,"atomic JSON complete");Interlocked.Increment(ref observations);}catch(FileNotFoundException){}catch(IOException ex)when(OperatingSystem.IsWindows()&&(ex.HResult&0xFFFF)is 32 or 33){Thread.Sleep(1);}}});
         for(int index=0;index<150;index++){settings.DisplayName="Generation "+index;Assert(Save(settings,path),"atomic save stress");}
         readerStop.Cancel();await reader;Assert(observations>0,"reader observations");Assert(!Directory.EnumerateFiles(directory,"*.tmp").Any(),"no residual temporary writes");
         var previous=File.ReadAllBytes(path);settings.MediaPort=0;Assert(!Save(settings,path),"invalid save error surfaced");Assert(previous.SequenceEqual(File.ReadAllBytes(path)),"rejected save keeps previous file intact");
         var blocked=Path.Combine(directory,"blocked");File.WriteAllText(blocked,"obstruction");settings.MediaPort=45679;Assert(!Save(settings,Path.Combine(blocked,"settings.json")),"filesystem save failure surfaced");
         Assert(!Directory.EnumerateFiles(directory,"*.tmp").Any(),"failure leaves no temp files");Console.WriteLine($"PASS 150 atomic saves/{observations} independent complete JSON reads, no temp residue; invalid/filesystem failure retains prior data and returns error.");
        }
        finally{if(!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new Exception("Cleanup escaped test directory");Directory.Delete(directory,true);}
        
    }
}

