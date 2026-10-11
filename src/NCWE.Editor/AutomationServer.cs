// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Pipes;
using System.Text.Json;

namespace NCWE.Editor;

// Replies must be materialized on the UI thread, where scene collections belong.
// Serializing a live dictionary later on the pipe worker races the next frame.
internal sealed class AutomationReply
{
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<string> Task => _completion.Task;
    public void SetResult(object response)
    {
        string json;
        try { json = JsonSerializer.Serialize(response, Options); }
        catch (Exception ex) { json = JsonSerializer.Serialize(new { ok = false, error = "Response serialization failed: " + ex.Message }); }
        _completion.SetResult(json);
    }
}

internal sealed class AutomationServer : IDisposable
{
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _task;
    public AutomationServer(string name,Func<JsonElement,Task<string>> command)
    {
        _task=Task.Run(async()=> {
            while(!_stop.IsCancellationRequested) {
                try {
                    using var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    using var reader=new StreamReader(pipe,leaveOpen:true);using var writer=new StreamWriter(pipe,leaveOpen:true){AutoFlush=true};
                    var line=await reader.ReadLineAsync(_stop.Token);
                    if(line is null)continue;
                    string response;
                    try {if(line.Length>65536)throw new InvalidDataException("Command too large.");using var json=JsonDocument.Parse(line);response=await command(json.RootElement.Clone());}
                    catch(Exception e){response=JsonSerializer.Serialize(new{ok=false,error=e.Message});}
                    await writer.WriteLineAsync(response);
                } catch(OperationCanceledException) {break;} catch(IOException) {} catch(ObjectDisposedException) {break;}
            }
        });
    }
    public void Dispose(){_stop.Cancel();}
}
