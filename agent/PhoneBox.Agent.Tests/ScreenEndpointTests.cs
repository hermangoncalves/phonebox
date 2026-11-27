using System.IO;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PhoneBox.Agent;
using Xunit;

public sealed class ScreenEndpointTests
{
    [Fact]
    public async Task ScreenGetRequiresAWebSocketUpgrade()
    {
        await using var rig = ScreenFeedsTestRig.Create(Path.GetTempPath());
        using var services = new ServiceCollection().AddSingleton(rig.Feeds).BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/screen";
        ctx.Request.QueryString = new QueryString("?widget=desk%2Fscreen&viewer=1");
        ctx.Request.Headers["X-PhoneBox-Device"] = "phone-a";
        await ScreenEndpoint.HandleAsync(ctx);
        Assert.Equal(400, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ScreenPostRecoverForeignHandleForbidden()
    {
        await using var rig = ScreenFeedsTestRig.Create(Path.GetTempPath());
        using var services = new ServiceCollection().AddSingleton(rig.Feeds).BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/api/screen";
        ctx.Request.QueryString = new QueryString("?widget=desk%2Fscreen&viewer=1");
        ctx.Request.Headers["X-PhoneBox-Device"] = "phone-a";
        ctx.Request.ContentType = "application/json";
        var body = "{\"op\":\"recover\",\"viewer\":2}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        await ScreenEndpoint.HandleAsync(ctx);
        Assert.Equal(403, ctx.Response.StatusCode);
    }
}
