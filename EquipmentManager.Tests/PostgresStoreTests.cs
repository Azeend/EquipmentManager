using System.Data;
using System.Data.Common;
using System.Security.Claims;
using EquipmentManager.Models;
using EquipmentManager.Services;
using Xunit;

namespace EquipmentManager.Tests;

/// <summary>Проверяет бизнес-логику новых хранилищ без сети; исполнение SQL/RLS требует отдельного PostgreSQL-стенда.</summary>
public sealed class PostgresStoreTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static ClaimsPrincipal User => new(new ClaimsIdentity([new Claim("sub", UserId.ToString())], "verified-test"));
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Ввод нормализуется без изменения объекта формы, а значения передаются параметрами.</summary>
    [Fact]
    public async Task EquipmentSave_TrimsAndUsesParameters()
    {
        var executor = new RecordingExecutor(EquipmentTable([7, "Насос", "INV-7", "Цех", "Исправно"]));
        var input = new Equipment { Name = " Насос ", InventoryNumber = " INV-7 ", Location = " Цех ", Status = "Исправно" };
        var result = await new PostgresEquipmentStore(executor).SaveAsync(User, input);
        Assert.Equal(EquipmentSaveStatus.Saved, result.Status);
        Assert.Equal(7, result.Equipment!.Id);
        Assert.Equal(" INV-7 ", input.InventoryNumber);
        Assert.Equal("INV-7", executor.Commands.Single().Parameters[1].Value);
        Assert.DoesNotContain("INV-7", executor.Commands[0].Sql);
        Assert.Equal(["engineer"], executor.Roles);
        Assert.True(executor.Completed);
    }

    /// <summary>Пустые поля/неизвестный статус не приводят к SQL-записи.</summary>
    [Theory]
    [InlineData(" ", "INV-7", "Цех", "Исправно")]
    [InlineData("Насос", "\t", "Цех", "Исправно")]
    [InlineData("Насос", "INV-7", " ", "Исправно")]
    [InlineData("Насос", "INV-7", "Цех", "Неизвестно")]
    public async Task EquipmentSave_InvalidInput_DoesNotExecute(string name, string number, string location, string status)
    {
        var executor = new RecordingExecutor();
        var result = await new PostgresEquipmentStore(executor).SaveAsync(User,
            new Equipment { Name = name, InventoryNumber = number, Location = location, Status = status });
        Assert.Equal(EquipmentSaveStatus.Invalid, result.Status);
        Assert.Empty(executor.Commands);
    }

    /// <summary>Только конфликт именно нормализованного номера преобразуется в бизнес-ошибку.</summary>
    [Theory]
    [InlineData("equipment_inventory_number_unique", true)]
    [InlineData("unexpected_unique", false)]
    public async Task EquipmentSave_ClassifiesExactUniqueConstraint(string constraint, bool expectedConflict)
    {
        var executor = new RecordingExecutor(new StoreException(StoreFailure.Database, "23505", constraint));
        var store = new PostgresEquipmentStore(executor);
        var input = new Equipment { Name = "Насос", InventoryNumber = "INV-7", Location = "Цех", Status = "Исправно" };
        if (expectedConflict) { Assert.Equal(EquipmentSaveStatus.Conflict, (await store.SaveAsync(User, input)).Status); }
        else { await Assert.ThrowsAsync<StoreException>(() => store.SaveAsync(User, input)); }
        Assert.False(executor.Completed);
    }

    /// <summary>FK-связь запрещает удаление; посторонняя ошибка не выдаётся за штатный запрет.</summary>
    [Theory]
    [InlineData("faults_equipment_id_fkey", true)]
    [InlineData("maintenance_records_equipment_id_fkey", true)]
    [InlineData("other_fk", false)]
    public async Task EquipmentDelete_UsesForeignKeyProtection(string constraint, bool linked)
    {
        var executor = new RecordingExecutor(new StoreException(StoreFailure.Database, "23503", constraint));
        var store = new PostgresEquipmentStore(executor);
        if (linked) { Assert.Equal(EquipmentDeleteStatus.Linked, await store.DeleteAsync(User, 7)); }
        else { await Assert.ThrowsAsync<StoreException>(() => store.DeleteAsync(User, 7)); }
    }

    /// <summary>Поисковая строка, включая спецсимволы, не встраивается в SQL.</summary>
    [Fact]
    public async Task EquipmentSearch_ParameterizesLiteralSubstring()
    {
        var executor = new RecordingExecutor(EquipmentTable());
        await new PostgresEquipmentStore(executor).GetAsync(User, "  ' OR 1=1 -- %_  ");
        Assert.Equal("' OR 1=1 -- %_", executor.Commands.Single().Parameters[0].Value);
        Assert.DoesNotContain("OR 1=1", executor.Commands[0].Sql);
    }

    /// <summary>Автор регистрации берётся из транзакционного контекста, время и подпись возвращаются из БД.</summary>
    [Fact]
    public async Task FaultRegister_UsesVerifiedActorAndDatabaseSnapshot()
    {
        var executor = new RecordingExecutor(FaultTable("Новая", 1));
        var result = await new PostgresFaultStore(executor).RegisterAsync(User,
            new FaultRegistration { EquipmentId = 7, Description = "  Вибрация  " });
        Assert.Equal(FaultWriteStatus.Saved, result.Status);
        Assert.Equal(UserId, executor.Commands.Single().Parameters[2].Value);
        Assert.Equal("Вибрация", executor.Commands[0].Parameters[1].Value);
        Assert.Equal(CreatedAt, result.Fault!.CreatedAt);
        Assert.Null(result.Fault.StartedAt);
        Assert.Equal(UserId.ToString(), result.Fault.Author);
        Assert.DoesNotContain("INSERT INTO public.maintenance_records", executor.Commands[0].Sql);
    }

    /// <summary>Оборудование, исчезнувшее до/во время INSERT, не создаёт заявку без связи.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultRegister_MissingEquipment_ReturnsNotFound(bool foreignKeyRace)
    {
        var executor = foreignKeyRace
            ? new RecordingExecutor(new StoreException(StoreFailure.Database, "23503", "faults_equipment_id_fkey"))
            : new RecordingExecutor(FaultTable());
        var result = await new PostgresFaultStore(executor).RegisterAsync(User, new FaultRegistration { EquipmentId = 7, Description = "Проблема" });
        Assert.Equal(FaultWriteStatus.NotFound, result.Status);
    }

    /// <summary>Пропуск этапа и обратный переход отклоняются до UPDATE.</summary>
    [Theory]
    [InlineData("Новая", "Завершена")]
    [InlineData("Завершена", "В работе")]
    public async Task FaultChange_RejectsInvalidTransition(string current, string target)
    {
        var executor = new RecordingExecutor(FaultTable(current, 1));
        var result = await new PostgresFaultStore(executor).ChangeStatusAsync(User, 11, 1, target,
            new FaultCompletion { WorkDescription = "Работы", Result = "Результат" });
        Assert.Equal(FaultWriteStatus.InvalidTransition, result.Status);
        Assert.Single(executor.Commands);
        Assert.StartsWith("SELECT", executor.Commands[0].Sql);
    }

    /// <summary>Завершение требует оба непустых поля; неизвестный статус не допускается.</summary>
    [Theory]
    [InlineData("Завершена", " ", "Готово", FaultWriteStatus.Invalid)]
    [InlineData("Завершена", "Работы", "\t", FaultWriteStatus.Invalid)]
    [InlineData("Отменена", "Работы", "Готово", FaultWriteStatus.InvalidTransition)]
    public async Task FaultChange_RejectsInvalidFieldsBeforeSql(string status, string work, string result, FaultWriteStatus expected)
    {
        var executor = new RecordingExecutor();
        var response = await new PostgresFaultStore(executor).ChangeStatusAsync(User, 11, 2, status,
            new FaultCompletion { WorkDescription = work, Result = result });
        Assert.Equal(expected, response.Status);
        Assert.Empty(executor.Commands);
    }

    /// <summary>Устаревшая версия не перезаписывает текущую заявку.</summary>
    [Fact]
    public async Task FaultChange_RejectsStaleVersion()
    {
        var executor = new RecordingExecutor(FaultTable("В работе", 3));
        var response = await new PostgresFaultStore(executor).ChangeStatusAsync(User, 11, 1, "В работе");
        Assert.Equal(FaultWriteStatus.Conflict, response.Status);
        Assert.Single(executor.Commands);
    }

    /// <summary>Гонка после чтения также обрабатывается как конфликт.</summary>
    [Fact]
    public async Task FaultChange_ZeroUpdatedRows_ReturnsConflict()
    {
        var executor = new RecordingExecutor(FaultTable("Новая", 1), FaultTable());
        var result = await new PostgresFaultStore(executor).ChangeStatusAsync(User, 11, 1, "В работе");
        Assert.Equal(FaultWriteStatus.Conflict, result.Status);
        Assert.Contains("version = $2 AND status = 'Новая'", executor.Commands[1].Sql);
    }

    /// <summary>Завершение выполняет один UPDATE; версия/даты и создание истории оставлены триггерам.</summary>
    [Fact]
    public async Task FaultCompletion_DoesNotInsertDuplicateHistory()
    {
        var executor = new RecordingExecutor(FaultTable("В работе", 2), FaultTable("Завершена", 3));
        var result = await new PostgresFaultStore(executor).ChangeStatusAsync(User, 11, 2, "Завершена",
            new FaultCompletion { WorkDescription = " Работы ", Result = " Исправно " });
        Assert.Equal(FaultWriteStatus.Saved, result.Status);
        Assert.Equal(3, result.Fault!.Version);
        Assert.Equal(TimeSpan.Zero, result.Fault.CompletedAt!.Value.Offset);
        Assert.Equal(2, executor.Commands.Count);
        var update = executor.Commands[1];
        Assert.Contains("version = $2 AND status = 'В работе'", update.Sql);
        Assert.DoesNotContain("version = version", update.Sql);
        Assert.DoesNotContain("INSERT INTO", update.Sql);
        Assert.Equal(UserId, update.Parameters[2].Value);
        Assert.Equal("Работы", update.Parameters[3].Value);
        Assert.Equal(["maintenance_specialist"], executor.Roles);
        Assert.True(executor.Completed);
    }

    /// <summary>Ошибка INSERT истории, возвращённая транзакцией, не превращается в успешное завершение.</summary>
    [Fact]
    public async Task FaultCompletion_PropagatesTriggerFailure()
    {
        var executor = new RecordingExecutor(FaultTable("В работе", 2), new StoreException(StoreFailure.Database, "23514"));
        await Assert.ThrowsAsync<StoreException>(() => new PostgresFaultStore(executor).ChangeStatusAsync(User, 11, 2,
            "Завершена", new FaultCompletion { WorkDescription = "Работы", Result = "Результат" }));
        Assert.False(executor.Completed);
    }

    /// <summary>История использует nullable-фильтр и стабильный порядок; дата читается с UTC offset.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(7)]
    public async Task MaintenanceRead_MapsAndFilters(int? equipmentId)
    {
        var table = Table([typeof(int), typeof(int), typeof(string), typeof(DateTimeOffset), typeof(string), typeof(string)],
            [11, 7, "Насос (INV-7)", CreatedAt, "Работы", "Готово"]);
        var executor = new RecordingExecutor(table);
        var rows = await new PostgresMaintenanceStore(executor).GetAsync(User, equipmentId);
        Assert.Equal(11, rows.Single().FaultId);
        Assert.Equal(CreatedAt, rows[0].PerformedAt);
        Assert.Equal(equipmentId, executor.Commands.Single().Parameters[0].Value);
        Assert.Contains("ORDER BY performed_at DESC, fault_id DESC", executor.Commands[0].Sql);
    }

    /// <summary>Ошибка источника не маскируется пустой историей или реестром.</summary>
    [Fact]
    public async Task StorageFailure_IsNotEmptySuccess()
    {
        var executor = new RecordingExecutor(new StoreException(StoreFailure.Unavailable));
        await Assert.ThrowsAsync<StoreException>(() => new PostgresMaintenanceStore(executor).GetAsync(User));
        Assert.False(executor.Completed);
    }

    /// <summary>Нельзя принять sub из неаутентифицированной identity или неоднозначный sub.</summary>
    [Theory]
    [InlineData("anonymous")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("ambiguous")]
    public void Context_RejectsUnverifiedOrAmbiguousSubject(string scenario)
    {
        var identity = new ClaimsIdentity(scenario == "anonymous" ? null : "verified-test");
        if (scenario != "missing") { identity.AddClaim(new Claim("sub", scenario == "empty" ? Guid.Empty.ToString() : UserId.ToString())); }
        if (scenario == "ambiguous") { identity.AddClaim(new Claim("sub", Guid.NewGuid().ToString())); }
        Assert.Throws<UnauthorizedAccessException>(() => PostgresStoreExecutor.GetVerifiedUserId(new ClaimsPrincipal(identity)));
    }

    /// <summary>CRUD нельзя выполнять через владельца, профильного читателя или небезопасное соединение.</summary>
    [Theory]
    [InlineData("postgres", "SSL Mode=VerifyFull")]
    [InlineData("service_role", "SSL Mode=VerifyFull")]
    [InlineData("equipment_app", "SSL Mode=VerifyFull")]
    [InlineData("equipment_api", "SSL Mode=Require")]
    [InlineData("equipment_api", "SSL Mode=VerifyFull;No Reset On Close=true")]
    [InlineData("equipment_api", "SSL Mode=VerifyFull;Include Error Detail=true")]
    [InlineData("equipment_api", "SSL Mode=VerifyFull;Multiplexing=true")]
    public void Connection_RejectsUnsafeCrudCredentials(string username, string options) =>
        Assert.Throws<InvalidOperationException>(() => PostgresStoreExecutor.ValidateConnectionString(
            $"Host=localhost;Database=postgres;Username={username};{options}"));

    /// <summary>Прямой и Supavisor-формат имени equipment_api допускаются без открытия соединения.</summary>
    [Theory]
    [InlineData("equipment_api")]
    [InlineData("equipment_api.projectref")]
    public void Connection_AcceptsRestrictedLogin(string username) =>
        PostgresStoreExecutor.ValidateConnectionString($"Host=localhost;Database=postgres;Username={username};SSL Mode=VerifyFull");

    private static DataTable EquipmentTable(params object[][] rows) => Table(
        [typeof(int), typeof(string), typeof(string), typeof(string), typeof(string)], rows);

    private static DataTable FaultTable(string? status = null, int version = 1) => Table(
        [typeof(int), typeof(int), typeof(string), typeof(string), typeof(string), typeof(string), typeof(DateTimeOffset),
            typeof(DateTimeOffset), typeof(DateTimeOffset), typeof(string), typeof(string), typeof(int)],
        status is null ? [] : [[11, 7, "Насос (INV-7)", "Вибрация", status, UserId.ToString(), CreatedAt,
            status == "Новая" ? DBNull.Value : CreatedAt.AddMinutes(1),
            status == "Завершена" ? CreatedAt.AddMinutes(2) : DBNull.Value,
            status == "Завершена" ? "Работы" : "", status == "Завершена" ? "Исправно" : "", version]]);

    private static DataTable Table(Type[] types, params object[][] rows)
    {
        var table = new DataTable();
        for (var index = 0; index < types.Length; index++) { table.Columns.Add("c" + index, types[index]); }
        foreach (var row in rows) { table.Rows.Add(row); }
        return table;
    }

    // Подмена границы SQL, а не эмулятор PostgreSQL: эти тесты не доказывают выполнение RLS/триггеров.
    private sealed class RecordingExecutor(params object[] responses) : IStoreExecutor, IStoreTransaction
    {
        private readonly Queue<object> responses = new(responses);
        public Guid UserId { get; private set; }
        internal List<(string Sql, IReadOnlyList<StoreParameter> Parameters)> Commands { get; } = [];
        internal IReadOnlyList<string> Roles { get; private set; } = [];
        internal bool Completed { get; private set; }

        public async Task<T> RunAsync<T>(ClaimsPrincipal user, IReadOnlyList<string> allowedRoles,
            Func<IStoreTransaction, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            UserId = PostgresStoreExecutor.GetVerifiedUserId(user);
            Roles = allowedRoles;
            var result = await operation(this, cancellationToken);
            Completed = true;
            return result;
        }

        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, IReadOnlyList<StoreParameter> parameters,
            Func<DbDataReader, T> map, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add((sql, parameters.ToArray()));
            var response = responses.Dequeue();
            if (response is Exception exception) { throw exception; }
            using var reader = ((DataTable)response).CreateDataReader();
            var rows = new List<T>();
            while (reader.Read()) { rows.Add(map(reader)); }
            return Task.FromResult<IReadOnlyList<T>>(rows);
        }
    }
}
