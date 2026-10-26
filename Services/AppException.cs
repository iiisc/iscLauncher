using System;

namespace iscLauncher.Services;

/// <summary>A failure whose message is written for the user, so the UI can show it as it is.</summary>
/// <param name="inner">The underlying error, kept for the log when the message is a summary of it.</param>
public class AppException(string message, Exception? inner = null) : Exception(message, inner);
