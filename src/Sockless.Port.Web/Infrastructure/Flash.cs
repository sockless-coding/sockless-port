using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>One-time messages after a form post (post/redirect/get), shown at the top of the next page.</summary>
public static class Flash
{
    private const string SuccessKey = "flash.success";
    private const string ErrorKey = "flash.error";

    public static void Success(ITempDataDictionary tempData, string message) => tempData[SuccessKey] = message;

    public static void Error(ITempDataDictionary tempData, string message) => tempData[ErrorKey] = message;

    public static (string? Success, string? Error) Take(ITempDataDictionary tempData) =>
        (tempData[SuccessKey] as string, tempData[ErrorKey] as string);
}
