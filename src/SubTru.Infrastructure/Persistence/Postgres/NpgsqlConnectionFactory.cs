using Npgsql;

namespace SubTru.Infrastructure.Persistence.Postgres;

public sealed class NpgsqlConnectionFactory
{
    private readonly string _connectionString;

    public NpgsqlConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public NpgsqlConnection Create()
        => new(_connectionString);
}
