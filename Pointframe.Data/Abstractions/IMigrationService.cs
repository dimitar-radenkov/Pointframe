namespace Pointframe.Data.Abstractions;

public interface IMigrationService
{
    Task ApplyMigrations();
}