using ClearPlate.MacCapture;
using System.Text.Json;

// Private inherited pipes only: never print these messages to a console or log.
if (!args.Contains("--stdio-capture") && !args.Contains("--self-test")) return 2;
using var capture = new WechatTokenCapture();
var gate = new object();
void Send(object value) { lock (gate) { Console.Out.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); } }
var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
capture.TokenFound += token => Send(new { type = "token", token });
capture.Diagnostic += _ => Send(new { type = "diagnostic" });
var port = capture.Start();
if (args.Contains("--self-test")) { Console.WriteLine("Loopback capture listener started; no system proxy or trust was changed."); return 0; }
Send(new { type = "ready", port, certificate = Convert.ToBase64String(capture.PublicRoot) });
_ = Task.Run(async () => { _ = await Console.In.ReadLineAsync(); finished.TrySetResult(); });
await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromMinutes(3)));
return 0;
