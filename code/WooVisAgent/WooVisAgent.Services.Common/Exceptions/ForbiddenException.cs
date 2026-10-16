using System;

namespace WooVisAgent.Services.Common.Exceptions;

public class ForbiddenException(string? message) : Exception(message);