using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Deals.BusinessLogic.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Deals.BusinessLogic.Context;

namespace Deals.BusinessLogic.Services
{
    public class Repository : IRepository
    {
        private readonly AppDbContext _context;

        public Repository(AppDbContext context)
        {
            _context = context;
            _context.Database.SetCommandTimeout(0);
        }

        #region Query Methods

        public IQueryable<T> Get<T>() where T : class
        {
            return _context.Set<T>().AsNoTracking();
        }

        public IQueryable<T> Get<T>(Expression<Func<T, bool>> predicate) where T : class
        {
            return _context.Set<T>().AsNoTracking().Where(predicate);
        }

        public DbSet<T> GetTrack<T>() where T : class
        {
            return _context.Set<T>();
        }

        #endregion

        #region GetById Methods

        public async Task<T?> GetByIdAsync<T>(int id) where T : class
        {
            return await FindByIdAsync<T>(id);
        }

        public async Task<T?> GetByIdAsync<T>(Guid id) where T : class
        {
            return await FindByIdAsync<T>(id);
        }

        #endregion

        #region Save and Update Methods

        public async Task<T> Save<T>(T model) where T : class
        {
            _context.Set<T>().Add(model);
            await _context.SaveChangesAsync();
            return model;
        }

        public async Task<T> SaveUpdate<T>(int id, T model) where T : class
        {
            return await SaveUpdateInternal(id, model);
        }

        public async Task<T> SaveUpdate<T>(Guid id, T model) where T : class
        {
            return await SaveUpdateInternal(id, model);
        }

        private async Task<T> SaveUpdateInternal<T>(object id, T model) where T : class
        {
            var existing = await FindByIdAsync<T>(id);
            if (existing != null)
            {
                _context.Entry(existing).CurrentValues.SetValues(model);
                await _context.SaveChangesAsync();
                return existing;
            }

            _context.Set<T>().Add(model);
            await _context.SaveChangesAsync();
            return model;
        }

        public async Task<T> AddOrUpdate<T>(T obj) where T : class
        {
            _context.Set<T>().Update(obj);
            await _context.SaveChangesAsync();
            return obj;
        }

        #endregion

        #region Dynamic Field Update Methods

        public async Task<bool> UpdateFieldAsync<T>(int id, string propertyName, object value) where T : class
        {
            var entity = await _context.Set<T>().FindAsync(id);
            if (entity == null) return false;

            var property = typeof(T).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null || !property.CanWrite) return false;

            var convertedValue = Convert.ChangeType(value, property.PropertyType);
            property.SetValue(entity, convertedValue);

            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> UpdateFieldAsync<T, TValue>(int id, Expression<Func<T, TValue>> propertySelector, TValue value) where T : class
        {
            var entity = await _context.Set<T>().FindAsync(id);
            if (entity == null) return false;

            if (propertySelector.Body is MemberExpression memberExpr)
            {
                var propertyName = memberExpr.Member.Name;
                var property = typeof(T).GetProperty(propertyName);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(entity, value);
                    await _context.SaveChangesAsync();
                    return true;
                }
            }
            return false;
        }

        public async Task<int> UpdateFieldsAsync<T>(int id, Dictionary<string, object> fields) where T : class
        {
            var entity = await _context.Set<T>().FindAsync(id);
            if (entity == null) return 0;

            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

            int updatedCount = 0;
            foreach (var field in fields)
            {
                if (properties.TryGetValue(field.Key, out var property) && property.CanWrite)
                {
                    var convertedValue = Convert.ChangeType(field.Value, property.PropertyType);
                    property.SetValue(entity, convertedValue);
                    updatedCount++;
                }
            }

            if (updatedCount > 0)
            {
                await _context.SaveChangesAsync();
            }
            return updatedCount;
        }

        public async Task<int> UpdateFieldsAsync<T>(T entity, Dictionary<string, object> fields) where T : class
        {
            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

            int updatedCount = 0;
            foreach (var field in fields)
            {
                if (properties.TryGetValue(field.Key, out var property) && property.CanWrite)
                {
                    var convertedValue = Convert.ChangeType(field.Value, property.PropertyType);
                    property.SetValue(entity, convertedValue);
                    updatedCount++;
                }
            }

            if (updatedCount > 0)
            {
                await _context.SaveChangesAsync();
            }
            return updatedCount;
        }

        #endregion

        #region Delete Methods

        public async Task<int> RemoveAsync<T>(T entity) where T : class
        {
            _context.Set<T>().Remove(entity);
            return await _context.SaveChangesAsync();
        }

        public async Task<int> RemoveAsync<T>(int id) where T : class
        {
            var entity = await FindByIdAsync<T>(id);
            if (entity == null) return 0;
            _context.Set<T>().Remove(entity);
            return await _context.SaveChangesAsync();
        }

        public async Task<int> RemoveRangeAsync<T>(List<T> entities) where T : class
        {
            _context.Set<T>().RemoveRange(entities);
            return await _context.SaveChangesAsync();
        }

        #endregion

        #region Collection Sync Methods

        public async Task<List<T>> SyncAsync<T>(List<T> newListModel, List<T> currentListModel, Func<T, object> keySelector) where T : class
        {
            var newDict = newListModel.ToDictionary(keySelector);
            var currentDict = currentListModel.ToDictionary(keySelector);

            var toRemove = currentDict.Keys.Except(newDict.Keys);
            foreach (var key in toRemove)
            {
                _context.Set<T>().Remove(currentDict[key]);
            }

            var toAdd = newDict.Keys.Except(currentDict.Keys);
            foreach (var key in toAdd)
            {
                _context.Set<T>().Add(newDict[key]);
            }

            await _context.SaveChangesAsync();

            var toUpdate = newDict.Keys.Intersect(currentDict.Keys);
            foreach (var key in toUpdate)
            {
                var updated = newDict[key];
                var current = currentDict[key];
                _context.Entry(current).CurrentValues.SetValues(updated);
            }

            if (toUpdate.Any())
            {
                await _context.SaveChangesAsync();
            }

            return newListModel;
        }

        public (List<T> ToAdd, List<T> ToRemove) DiffList<T, TKey>(IEnumerable<T> original, IEnumerable<T> updated, Func<T, TKey> keySelector) where TKey : notnull
        {
            var oldDict = original.ToDictionary(keySelector);
            var newDict = updated.ToDictionary(keySelector);

            var addKeys = newDict.Keys.Except(oldDict.Keys);
            var removeKeys = oldDict.Keys.Except(newDict.Keys);

            var toAdd = addKeys.Select(k => newDict[k]).ToList();
            var toRemove = removeKeys.Select(k => oldDict[k]).ToList();

            return (toAdd, toRemove);
        }

        #endregion

        #region Raw SQL and Unit of Work

        public async Task<int> ExecuteSqlRawAsync(string sql, params object[] parameters)
        {
            return await _context.Database.ExecuteSqlRawAsync(sql, parameters);
        }

        public async Task<List<T>> SqlQueryAsync<T>(string sql, params object[] parameters) where T : class
        {
            return await _context.Database.SqlQueryRaw<T>(sql, parameters).ToListAsync();
        }

        public async Task<int> SaveChangesAsync()
        {
            return await _context.SaveChangesAsync();
        }

        public async Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> operation)
        {
            if (_context.Database.CurrentTransaction != null)
            {
                return await operation();
            }

            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var result = await operation();
                await transaction.CommitAsync();
                return result;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        #endregion

        #region Private Helpers

        private async Task<T?> FindByIdAsync<T>(object id) where T : class
        {
            return await _context.Set<T>().FindAsync(id);
        }

        #endregion

    }
}
