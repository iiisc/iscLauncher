using System;

namespace iscLauncher.Services;

/// <summary>A failure whose message is written for the user, so the UI can show it as it is.</summary>
public class AppException(string message) : Exception(message);
