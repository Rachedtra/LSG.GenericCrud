﻿using System;
using System.Threading.Tasks;
using LSG.GenericCrud.Models;
using Microsoft.AspNetCore.Mvc;

namespace LSG.GenericCrud.Extensions.Controllers
{
    public interface IReadeableCrudController<T> where T : class, IEntity, new()
    {
        Task<IActionResult> GetAllReadStatus();
        Task<IActionResult> MarkAllAsRead();
        Task<IActionResult> MarkAllAsUnread();
        Task<IActionResult> MarkOneAsRead(Guid id);
        Task<IActionResult> MarkOneAsUnread(Guid id);
    }
}