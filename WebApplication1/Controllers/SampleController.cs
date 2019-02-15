﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using LSG.GenericCrud.Controllers;
using LSG.GenericCrud.Exceptions;
using LSG.GenericCrud.Extensions.Controllers;
using LSG.GenericCrud.Extensions.DataFillers;
using LSG.GenericCrud.Models;
using LSG.GenericCrud.Repositories;
using LSG.GenericCrud.Services;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using WebApplication1.Models;
using IUserInfoRepository = LSG.GenericCrud.Extensions.DataFillers.IUserInfoRepository;

namespace WebApplication1.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SampleController :
        ControllerBase,
        ICrudController<Account>,
        IReadeableCrudController<Account>
    {
        private readonly IHistoricalCrudController<Account> _crudController;
        private readonly IReadeableCrudController<Account> _readeableCrudController;
        private readonly ICrudRepository _repository;
        private readonly IUserInfoRepository _userInfoRepository;
        private readonly ICrudService<Account> _crudService;

        public SampleController(
            IHistoricalCrudController<Account> crudController,
            IReadeableCrudController<Account> readeableCrudController,
            ICrudRepository repository,
            IUserInfoRepository userInfoRepository,
            ICrudService<Account> crudService)
        {
            _crudController = crudController;
            _readeableCrudController = readeableCrudController;
            _repository = repository;
            _userInfoRepository = userInfoRepository;
            _crudService = crudService;
        }

        [HttpPost]
        public async Task<ActionResult<Account>> Create([FromBody] Account entity) => await _crudController.Create(entity);
        [HttpDelete("{id}")]
        public async Task<ActionResult<Account>> Delete(Guid id) => await _crudController.Delete(id);
        [HttpGet]
        public async Task<ActionResult<IEnumerable<Account>>> GetAll() => await _crudController.GetAll();
        [Route("{id}")]
        [HttpGet]
        public async Task<ActionResult<Account>> GetById(Guid id) => await _crudController.GetById(id);

        [HttpHead("{id}")]
        public async Task<IActionResult> Head(Guid id)
        {
            try
            {
                await _crudService.GetByIdAsync(id);
                return NoContent();
            }
            catch (EntityNotFoundException ex)
            {
                return NotFound();
            }
        }
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(Guid id, [FromBody] Account entity) => await _crudController.Update(id, entity);
        [HttpPost("{id}/restore")]
        public async Task<IActionResult> Restore(Guid id) => await _crudController.Restore(id);
        [HttpGet("{id}/history")]
        public virtual async Task<IActionResult> GetHistory(Guid id) => await _crudController.GetHistory(id);

        [HttpGet("readstatus")]
        public async Task<IActionResult> GetAllReadStatus() => await _readeableCrudController.GetAllReadStatus();
        [HttpPost]
        [Route("read")]
        public async Task<IActionResult> MarkAllAsRead() => await _readeableCrudController.MarkAllAsRead();
        [HttpPost]
        [Route("unread")]
        public async Task<IActionResult> MarkAllAsUnread() => await _readeableCrudController.MarkAllAsUnread();
        [HttpPost]
        [Route("{id}/read")]
        public async Task<IActionResult> MarkOneAsRead(Guid id) => await _readeableCrudController.MarkOneAsRead(id);
        [HttpPost]
        [Route("{id}/unread")]
        public async Task<IActionResult> MarkOneAsUnread(Guid id) => await _readeableCrudController.MarkOneAsUnread(id);

        [HttpPost("{id}/delta")]
        public async Task<IActionResult> PostDeltaRequest(Guid id, [FromBody] DeltaRequest request)
        {
            try
            {
                if (request.From == null) request.From = GetLastTimeViewed<Account>(id);
                if (request.To == null) request.To = DateTime.MaxValue;
                if (request.Mode == DeltaRequestModes.Snapshot) return await GetDeltaSnapshot(id, request.From.Value, request.To.Value);
                else if (request.Mode == DeltaRequestModes.Differential) return await GetDeltaDifferential(id, request.From.Value, request.To.Value);
                else return BadRequest("Unsupported mode");
            }
            catch (NoHistoryException ex)
            {
                return NoContent();
            }
        }

        private DateTime GetLastTimeViewed<T>(Guid id)
        {
            var lastView = _repository
                .GetAll<EntityUserStatus>()
                .SingleOrDefault(_ => _.EntityId == id && _.EntityName == typeof(T).FullName && _.UserId == _userInfoRepository.GetUserInfo());

            return lastView.LastViewed == null ? DateTime.MinValue : lastView.LastViewed.Value;
        }

        public async Task<IActionResult> GetDeltaSnapshot(Guid id, [FromQuery] DateTime fromTimestamp, [FromQuery] DateTime toTimestamp)
        {
            if (toTimestamp == DateTime.MinValue) toTimestamp = DateTime.MaxValue;

            var events = _repository
                .GetAll<HistoricalEvent>()
                .Where(_ => _.EntityId == id && _.CreatedDate >= fromTimestamp && _.CreatedDate <= toTimestamp)
                .OrderBy(_ => _.CreatedDate);

            if (events.Count() == 0) throw new NoHistoryException();

            SnapshotChangeset snapshotChangeset = ExtractSnapshotChanges(events, _repository.GetById<Account>(id));

            return Ok(snapshotChangeset);
        }


        public async Task<IActionResult> GetDeltaDifferential(Guid id, [FromQuery] DateTime fromTimestamp, [FromQuery] DateTime toTimestamp)
        {
            if (toTimestamp == DateTime.MinValue) toTimestamp = DateTime.MaxValue;

            // snapshot from creation date
            var events = _repository
                .GetAll<HistoricalEvent>()
                .Where(_ => _.EntityId == id && _.CreatedDate >= fromTimestamp && _.CreatedDate <= toTimestamp)
                .OrderBy(_ => _.CreatedDate);

            if (events.Count() == 0) throw new NoHistoryException();
            //.Skip(1);

            var differentialChangeset = ExtractDifferentialChangeset<Account>(id, events);

            return Ok(differentialChangeset);
        }

        private DifferentialChangeset ExtractDifferentialChangeset<T>(Guid id, IOrderedEnumerable<HistoricalEvent> events) where T : class, IEntity, new()
        {
            var sourceEvent = events.First();
            var sourceObject = sourceEvent.OriginalObject == null ? JsonConvert.DeserializeObject<T>(sourceEvent.Changeset) : JsonConvert.DeserializeObject<T>(sourceEvent.OriginalObject);
            var differentialChangeset = new DifferentialChangeset();
            differentialChangeset.EntityId = id;
            differentialChangeset.EntityTypeName = sourceEvent.EntityName;
            differentialChangeset.LastViewed = events.Last().CreatedDate.Value;
            differentialChangeset.Changesets = ExtractDifferentialChanges(id, events, sourceEvent, sourceObject);
            return differentialChangeset;
        }

        private List<Changeset> ExtractDifferentialChanges<T>(Guid id, IOrderedEnumerable<HistoricalEvent> events, HistoricalEvent sourceEvent, T sourceObject) where T : class, IEntity, new()
        {

            var differentialChangeset = new List<Changeset>();

            var currentEvent = sourceEvent;
            var currentObject = sourceObject;
            for (int i = 1; i < events.Count(); i++)
            {
                var nextEvent = events.ToArray()[i];
                var nextEventObject = JsonConvert.DeserializeObject<T>(currentEvent.Changeset);
                var changeset = new Changeset();
                changeset.Date = currentEvent.CreatedDate.Value;
                changeset.UserId = currentEvent.CreatedBy;
                changeset.EventName = currentEvent.Action;
                changeset.Changes = ExtractChanges(currentObject, nextEventObject);
                differentialChangeset.Add(changeset);

                currentObject = nextEventObject;
                currentEvent = nextEvent;
            }
            // add last event to current object
            var lastChangeset = new Changeset();
            var lastEvent = events.Last();

            lastChangeset.Date = lastEvent.CreatedDate.Value;
            lastChangeset.UserId = lastEvent.CreatedBy;
            lastChangeset.Changes = lastEvent.Action != HistoricalActions.Delete.ToString() ? ExtractChanges(currentObject, _repository.GetById<T>(id)) : null;
            lastChangeset.EventName = lastEvent.Action;

            differentialChangeset.Add(lastChangeset);

            return differentialChangeset;
        }

        private static SnapshotChangeset ExtractSnapshotChanges<T>(IOrderedEnumerable<HistoricalEvent> events, T actual)
        {
            // base line compararer
            var sourceEvent = events.FirstOrDefault();
            var sourceObject = sourceEvent.OriginalObject == null ? JsonConvert.DeserializeObject<T>(sourceEvent.Changeset) : JsonConvert.DeserializeObject<T>(sourceEvent.OriginalObject);

            var snapshotChangeset = new SnapshotChangeset();
            snapshotChangeset.EntityTypeName = sourceEvent.EntityName;
            snapshotChangeset.EntityId = sourceEvent.EntityId;
            snapshotChangeset.LastViewed = DateTime.Now; // TODO: Get Last Viewed Info from read status (if available)
            snapshotChangeset.LastModifiedBy = events.Last().CreatedBy;
            snapshotChangeset.LastModifiedEvent = events.Last().Action;
            snapshotChangeset.LastModifiedDate = events.Last().CreatedDate.Value;
            snapshotChangeset.Changes = ExtractChanges(sourceObject, actual);
            return snapshotChangeset;
        }

        private static List<Change> ExtractChanges<T>(T source, T destination)
        {
            var changes = new List<Change>();
            if (destination != null)
            {
                destination
                    .GetType()
                    .GetProperties()
                    .Where(_ => _.DeclaringType == destination.GetType())
                    .ToList()
                    .ForEach(_ => changes.Add(new Change()
                    {
                        FieldName = _.Name,
                        FromValue = source.GetType().GetProperty(_.Name).GetValue(source),
                        ToValue = destination.GetType().GetProperty(_.Name).GetValue(destination)
                    }));
            }
            return changes;
        }


    }

    [Serializable]
    internal class NoHistoryException : Exception
    {
        public NoHistoryException()
        {
        }

        public NoHistoryException(string message) : base(message)
        {
        }

        public NoHistoryException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected NoHistoryException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    public enum DeltaRequestModes
    {
        Snapshot,
        Differential
    }

    public class DeltaRequest
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public DeltaRequestModes Mode { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
    }

    internal class SnapshotChangeset
    {

        public string EntityTypeName { get; internal set; }
        public Guid EntityId { get; internal set; }
        public DateTime LastViewed { get; internal set; }
        public List<Change> Changes { get; internal set; }
        public DateTime LastModifiedDate { get; internal set; }
        public string LastModifiedBy { get; internal set; }
        public string LastModifiedEvent { get; internal set; }
    }
    internal class DifferentialChangeset
    {
        public string EntityTypeName { get; internal set; }
        public Guid EntityId { get; internal set; }
        public DateTime LastViewed { get; internal set; }

        public List<Changeset> Changesets { get; set; }
    }
    internal class Changeset
    {
        public string UserId { get; set; }
        public DateTime Date { get; set; }
        public List<Change> Changes { get; set; }
        public string EventName { get; internal set; }
    }
    internal class Change
    {
        public string FieldName { get; set; }
        public object FromValue { get; set; }
        public object ToValue { get; set; }
    }


}
