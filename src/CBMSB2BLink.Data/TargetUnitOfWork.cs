using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using Microsoft.Data.SqlClient;

namespace CBMSB2BLink.Data;

public sealed class TargetUnitOfWork : ITargetUnitOfWork
{
    private readonly string _connectionString;
    private SqlConnection? _connection;
    private SqlTransaction? _transaction;
    private bool _completed;

    public TargetUnitOfWork(string connectionString)
    {
        _connectionString = connectionString;
    }

    public DbTransaction Transaction =>
        _transaction ?? throw new InvalidOperationException("Call InitializeAsync before using the transaction.");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _connection = new SqlConnection(_connectionString);
        await _connection.OpenAsync(cancellationToken);
        _transaction = (SqlTransaction)await _connection.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null)
        {
            throw new InvalidOperationException("Call InitializeAsync before committing.");
        }

        await _transaction.CommitAsync(cancellationToken);
        _completed = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null || _completed)
        {
            return;
        }

        await _transaction.RollbackAsync(cancellationToken);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed && _transaction is not null)
        {
            try
            {
                await _transaction.RollbackAsync();
            }
            catch
            {
                // Connection may already be broken; nothing more we can do here.
            }
        }

        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    internal SqlConnection Connection =>
        _connection ?? throw new InvalidOperationException("Call InitializeAsync before using the connection.");
}
