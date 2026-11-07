namespace KassirERP.Application.Abstractions;

/// <summary>Неверный запрос клиента (API отвечает 400 с этим текстом).</summary>
public class AppValidationException(string message) : Exception(message);
