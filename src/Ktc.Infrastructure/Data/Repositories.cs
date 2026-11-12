using System.Linq.Expressions;
using Ktc.Application.Abstractions;
using Ktc.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ktc.Infrastructure.Data;

public class EfRepository<T> : IRepository<T> where T : Entity
{
    private readonly KtcDbContext _db;
    public EfRepository(KtcDbContext db) => _db = db;
    public IQueryable<T> Query() => _db.Set<T>();
    public async Task<T?> GetAsync(int id, CancellationToken ct = default) => await _db.Set<T>().FindAsync(new object[] { id }, ct);
    public Task<List<T>> ListAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default) =>
        (predicate == null ? _db.Set<T>() : _db.Set<T>().Where(predicate)).ToListAsync(ct);
    public async Task AddAsync(T entity, CancellationToken ct = default) => await _db.Set<T>().AddAsync(entity, ct);
    public void Update(T entity) => _db.Set<T>().Update(entity);
    public void Remove(T entity) => _db.Set<T>().Remove(entity);
}

public class UnitOfWork : IUnitOfWork
{
    private readonly KtcDbContext _db;
    private readonly IServiceProvider _sp;
    public UnitOfWork(KtcDbContext db, IServiceProvider sp) { _db = db; _sp = sp; }
    public IRepository<T> Repo<T>() where T : Entity => (IRepository<T>)_sp.GetService(typeof(IRepository<T>))!;
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
