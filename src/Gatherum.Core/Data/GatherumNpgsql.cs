using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Gatherum.Core.Data;

/// <summary>The provider settings every <see cref="GatherumDbContext"/> needs, wherever
/// one is built — the app, the design-time factory, the tests. Gathered here because
/// missing <c>UseVector</c> in any one of them fails at model build with an error about
/// value converters that says nothing about vectors.</summary>
public static class GatherumNpgsql
{
    public static void Configure(NpgsqlDbContextOptionsBuilder npgsql)
    {
        npgsql.MigrationsAssembly("Gatherum.Infrastructure");
        npgsql.UseVector();
    }
}
