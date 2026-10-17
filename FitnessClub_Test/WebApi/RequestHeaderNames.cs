using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace WebApi;

public static class RequestHeaders
{
    public const string IdempotencyKey = "Idempotency-Key";
}
