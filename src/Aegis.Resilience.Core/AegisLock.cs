// Her çağrıda alınan kilitlerin tipi (tek tanım). .NET 9+'da System.Threading.Lock, Monitor'a göre daha hafiftir ve
// C# lock ifadesiyle aynı sözdizimiyle kullanılır; eski hedeflerde nesne kilidi. Yalnızca lock ifadesiyle kullanılır
// (Monitor.Wait/Pulse/TryEnter gereken yerlerde kullanılmaz).
#if NET9_0_OR_GREATER
global using AegisLock = System.Threading.Lock;
#else
global using AegisLock = System.Object;
#endif
