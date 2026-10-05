using Shop.Backends;

// Tek başına çalıştırma: dotnet run -- eu
var instance = args.FirstOrDefault() ?? "eu";
await using var backend = await BackendApp.StartAsync(instance);
Console.WriteLine($"Arka uç '{instance}': REST {backend.HttpAddress}, gRPC {backend.GrpcAddress}");
await Task.Delay(Timeout.Infinite);
