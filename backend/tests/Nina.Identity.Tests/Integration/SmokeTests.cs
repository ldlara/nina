using System.Net;
using Nina.Identity.Tests.Infrastructure;

namespace Nina.Identity.Tests.Integration;

public sealed class SmokeTests(PostgresFixture postgres) : IntegrationTestBase(postgres)
{
    [Fact]
    public async Task Register_verify_and_get_me_work_end_to_end()
    {
        var session = await Api.RegisterAndVerifyAsync();
        var me = await Api.GetAsync("/v1/me", session.AccessToken);

        Assert.Equal(HttpStatusCode.OK, me.Status);
        Assert.Equal(session.Email, me.Json!["email"]!.GetValue<string>());
        Assert.True(me.Json["email_verified"]!.GetValue<bool>());
    }
}
