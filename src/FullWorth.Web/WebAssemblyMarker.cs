namespace FullWorth.Web;

/// <summary>
/// Stable public marker for WebApplicationFactory-based integration tests.
/// Avoids binding tests to top-level Program types from referenced unified modules.
/// </summary>
public sealed class WebAssemblyMarker;
