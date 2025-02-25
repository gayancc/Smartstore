﻿using System.Linq.Dynamic.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.OData;
using Smartstore.ComponentModel;

namespace Smartstore.Web.Api
{
    /// <summary>
    /// Smart base controller class for OData endpoints.
    /// </summary>
    /// <remarks>
    /// - ActionResult<T> vs. IActionResult: IActionResult is used when multiple return types are possible.
    /// For ActionResult<T> ProducesResponseTypeAttribute's type property can be excluded.
    /// - Explicit "From" parameter bindings are required otherwise Swagger will describe them as "query" params by default.
    /// </remarks>
    [Authorize(AuthenticationSchemes = "Smartstore.WebApi.Basic")]
    public abstract class SmartODataController<TEntity> : ODataController
        where TEntity : BaseEntity, new()
    {
        internal const string FulfillKey = "SmApiFulfill";

        private SmartDbContext _db;
        private DbSet<TEntity> _dbSet;

        protected SmartDbContext Db
        {
            get => _db ??= HttpContext.RequestServices.GetService<SmartDbContext>();
        }

        protected DbSet<TEntity> Entities
        {
            get => _dbSet ??= Db.Set<TEntity>();
            set => _dbSet = value;
        }

        protected async Task<IActionResult> GetByIdAsync(int id, bool tracked = false)
        {
            var entity = await Entities.FindByIdAsync(id, tracked);

            // INFO: "NotFound" without object parameter produces unwanted HTML response.
            return entity != null
                ? Ok(entity)
                : NotFound(default(TEntity));
        }

        protected async Task<IActionResult> GetPropertyValueAsync(int key, string property)
        {
            Guard.NotEmpty(property, nameof(property));

            var values = await Entities
                .Where(x => x.Id == key)
                .Select($"new {{ {property} }}")
                .ToDynamicArrayAsync();

            if (values.IsNullOrEmpty())
            {
                return NotFound(default(TEntity));
            }

            var propertyValue = (values[0] as DynamicClass).GetDynamicPropertyValue(property);

            return Ok(propertyValue);
        }

        /// <summary>
        /// Adds an entity.
        /// </summary>
        /// <param name="entity">Entity to add.</param>
        /// <param name="add">
        /// Function called to save changes. Typically used to call Db.SaveChangesAsync().
        /// <c>null</c> executes SaveChangesAsync internally.
        /// </param>
        /// <returns>CreatedODataResult<TEntity> which leads to status code 201 "Created" including entity content.</returns>
        protected async Task<IActionResult> PostAsync(TEntity entity, Func<Task> add = null)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            Entities.Add(entity);
            entity = await ApplyRelatedEntityIdsAsync(entity);

            if (add != null)
            {
                await add();
            }
            else
            {
                await Db.SaveChangesAsync();
            }

            return Created(entity);
        }

        /// <summary>
        /// Updates an entity.
        /// </summary>
        /// <param name="key">Key (ID) of the entity.</param>
        /// <param name="model">Model with the data to overwrite the original entity.</param>
        /// <param name="update">
        /// Function called to save changes. Typically used to call Db.SaveChangesAsync().
        /// <c>null</c> executes SaveChangesAsync internally.
        /// </param>
        /// <returns>
        /// UpdatedODataResult<TEntity> which leads to:
        /// status code 204 "No Content" by default or
        /// status code 200 including entity content if "Prefer" header is specified with value "return=representation".
        /// </returns>
        protected Task<IActionResult> PutAsync(int key, Delta<TEntity> model, Func<TEntity, Task> update = null)
            => UpdateInternal(false, key, model, update);

        /// <summary>
        /// Partially updates an entity.
        /// </summary>
        /// <param name="key">Key (ID) of the entity.</param>
        /// <param name="model">Delta model with the data to overwrite the original entity.</param>
        /// <param name="update">
        /// Function called to save changes. Typically used to call Db.SaveChangesAsync().
        /// <c>null</c> executes SaveChangesAsync internally.
        /// </param>
        /// <returns>
        /// UpdatedODataResult<TEntity> which leads to:
        /// status code 204 "No Content" by default or
        /// status code 200 including entity content if "Prefer" header is specified with value "return=representation".
        /// </returns>
        protected Task<IActionResult> PatchAsync(int key, Delta<TEntity> model, Func<TEntity, Task> update = null)
            => UpdateInternal(true, key, model, update);

        private async Task<IActionResult> UpdateInternal(bool patch, int key, Delta<TEntity> model, Func<TEntity, Task> update)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var entity = await Entities.FindByIdAsync(key);
            if (entity == null)
            {
                return NotFound(default(TEntity));
            }

            try
            {
                if (patch)
                {
                    model.Patch(entity);
                }
                else
                {
                    model.Put(entity);
                }

                // TODO: (mg) (core) test ApplyRelatedEntityIdsAsync.
                entity = await ApplyRelatedEntityIdsAsync(entity);

                if (update != null)
                {
                    await update(entity);
                }
                else
                {
                    await Db.SaveChangesAsync();
                }
            }
            catch (DbUpdateConcurrencyException ex)
            {
                if (!await Entities.AnyAsync(x => x.Id == key))
                {
                    return NotFound(default(TEntity));
                }
                else
                {
                    return Conflict(ex.Message);
                }
            }
            catch (Exception ex)
            {
                return UnprocessableEntity(ex.Message);
            }

            return Updated(entity);
        }

        /// <summary>
        /// Deletes an entity.
        /// </summary>
        /// <param name="key">Key (ID) of the entity.</param>
        /// <param name="update">
        /// Function called to save changes. Typically used for soft deletable entities.
        /// <c>null</c> removes the entity and executes SaveChangesAsync internally.
        /// </param>
        /// <returns>NoContentResult which leads to status code 204.</returns>
        protected async Task<IActionResult> DeleteAsync(int key, Func<TEntity, Task> delete = null)
        {
            var entity = await Entities.FindByIdAsync(key);
            if (entity == null)
            {
                return NotFound(default(TEntity));
            }

            if (delete != null)
            {
                await delete(entity);
            }
            else
            {               
                Entities.Remove(entity);
                await Db.SaveChangesAsync();
            }

            return NoContent();
        }

        #region Utilities

        protected async Task<TEntity> ApplyRelatedEntityIdsAsync(TEntity entity)
        {
            if (entity != null)
            {
                var entityType = entity.GetType();

                foreach (var pair in Request.Query.Where(x => x.Key.StartsWithNoCase(FulfillKey)))
                {
                    var propName = pair.Key[FulfillKey.Length..];
                    var queryValue = pair.Value.ToString();

                    if (propName.HasValue() && queryValue.HasValue())
                    {
                        propName = propName.EnsureEndsWith("Id", StringComparison.OrdinalIgnoreCase);

                        var prop = FastProperty.GetProperty(entityType, propName);
                        if (prop != null)
                        {
                            var propType = prop?.Property?.PropertyType;
                            if (propType == typeof(int) || propType == typeof(int?))
                            {
                                var propValue = prop.GetValue(entity);
                                if (propValue == null || propValue.Equals(0))
                                {
                                    var relatedEntityId = await GetRelatedEntityIdAsync(propName, queryValue);
                                    if (relatedEntityId != 0)
                                    {
                                        prop.SetValue(entity, relatedEntityId);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return entity;
        }

        protected async Task<int> GetRelatedEntityIdAsync(string propertyName, string queryValue)
        {
            if (propertyName.EqualsNoCase("CountryId"))
            {
                return await Db.Countries
                    .ApplyIsoCodeFilter(queryValue)
                    .Select(x => x.Id)
                    .FirstOrDefaultAsync();
            }
            else if (propertyName.EqualsNoCase("StateProvinceId"))
            {
                return await Db.StateProvinces
                    .Where(x => x.Abbreviation == queryValue)
                    .Select(x => x.Id)
                    .FirstOrDefaultAsync();
            }
            else if (propertyName.EqualsNoCase("LanguageId"))
            {
                return await Db.Languages
                    .Where(x => x.LanguageCulture == queryValue)
                    .Select(x => x.Id)
                    .FirstOrDefaultAsync();
            }
            else if (propertyName.EqualsNoCase("CurrencyId"))
            {
                return await Db.Currencies
                    .Where(x => x.CurrencyCode == queryValue)
                    .Select(x => x.Id)
                    .FirstOrDefaultAsync();
            }

            return 0;
        }

        #endregion

        #region Probably obsolete (not required anymore)

        //protected IQueryable<TCollection> GetRelatedCollection<TCollection>(
        //    int key,
        //    Expression<Func<TEntity, IEnumerable<TCollection>>> navigationProperty)
        //{
        //    Guard.NotNull(navigationProperty, nameof(navigationProperty));

        //    var query = Entities.Where(x => x.Id.Equals(key));

        //    return query.SelectMany(navigationProperty);
        //}

        #endregion
    }
}
