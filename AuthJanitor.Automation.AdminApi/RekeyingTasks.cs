﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using AuthJanitor.Automation.Shared;
using AuthJanitor.Automation.Shared.DataStores;
using AuthJanitor.Automation.Shared.Models;
using AuthJanitor.Automation.Shared.ViewModels;
using AuthJanitor.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Http;

namespace AuthJanitor.Automation.AdminApi
{
    /// <summary>
    /// API functions to control the creation management, and approval of Rekeying Tasks.
    /// A Rekeying Task is a time-bounded description of one or more Managed Secrets to be rekeyed.
    /// </summary>
    public class RekeyingTasks : StorageIntegratedFunction
    {
        private readonly AuthJanitorServiceConfiguration _serviceConfiguration;
        private readonly TaskExecutionManager _taskExecutionManager;
        private readonly ProviderManagerService _providerManager;
        private readonly EventDispatcherService _eventDispatcher;

        public RekeyingTasks(
            AuthJanitorServiceConfiguration serviceConfiguration,
            TaskExecutionManager taskExecutionManager,
            EventDispatcherService eventDispatcher,
            ProviderManagerService providerManager,
            IDataStore<ManagedSecret> managedSecretStore,
            IDataStore<Resource> resourceStore,
            IDataStore<RekeyingTask> rekeyingTaskStore,
            Func<ManagedSecret, ManagedSecretViewModel> managedSecretViewModelDelegate,
            Func<Resource, ResourceViewModel> resourceViewModelDelegate,
            Func<RekeyingTask, RekeyingTaskViewModel> rekeyingTaskViewModelDelegate,
            Func<AuthJanitorProviderConfiguration, ProviderConfigurationViewModel> configViewModelDelegate,
            Func<ScheduleWindow, ScheduleWindowViewModel> scheduleViewModelDelegate,
            Func<LoadedProviderMetadata, LoadedProviderViewModel> providerViewModelDelegate) :
                base(managedSecretStore, resourceStore, rekeyingTaskStore, managedSecretViewModelDelegate, resourceViewModelDelegate, rekeyingTaskViewModelDelegate, configViewModelDelegate, scheduleViewModelDelegate, providerViewModelDelegate)
        {
            _serviceConfiguration = serviceConfiguration;
            _taskExecutionManager = taskExecutionManager;
            _eventDispatcher = eventDispatcher;
            _providerManager = providerManager;
        }

        [ProtectedApiEndpoint]
        [FunctionName("RekeyingTasks-Create")]
        public async Task<IActionResult> Create(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "tasks/{secretId:guid}")] Guid secretId,
            HttpRequest req)
        {
            if (!req.IsValidUser(AuthJanitorRoles.ServiceOperator, AuthJanitorRoles.GlobalAdmin)) return new UnauthorizedResult();

            if (!await ManagedSecrets.ContainsIdAsync(secretId))
            {
                await _eventDispatcher.DispatchEvent(AuthJanitorSystemEvents.AnomalousEventOccurred, nameof(AdminApi.RekeyingTasks.Create), "Secret ID not found");
                return new NotFoundObjectResult("Secret not found!");
            }

            var secret = await ManagedSecrets.GetAsync(secretId);
            if (!secret.TaskConfirmationStrategies.HasFlag(TaskConfirmationStrategies.AdminCachesSignOff) &&
                !secret.TaskConfirmationStrategies.HasFlag(TaskConfirmationStrategies.AdminSignsOffJustInTime))
            {
                await _eventDispatcher.DispatchEvent(AuthJanitorSystemEvents.AnomalousEventOccurred, nameof(AdminApi.ManagedSecrets.Create), "Managed Secret does not support adminstrator approval");
                return new BadRequestErrorMessageResult("Managed Secret does not support administrator approval!");
            }

            RekeyingTask newTask = new RekeyingTask()
            {
                Queued = DateTimeOffset.UtcNow,
                Expiry = secret.Expiry,
                ManagedSecretId = secret.ObjectId
            };

            await RekeyingTasks.CreateAsync(newTask);

            await _eventDispatcher.DispatchEvent(AuthJanitorSystemEvents.RotationTaskCreatedForApproval, nameof(AdminApi.ManagedSecrets.Create), newTask);

            return new OkObjectResult(newTask);
        }

        [ProtectedApiEndpoint]
        [FunctionName("RekeyingTasks-List")]
        public async Task<IActionResult> List(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tasks")] HttpRequest req)
        {
            if (!req.IsValidUser()) return new UnauthorizedResult();

            return new OkObjectResult((await RekeyingTasks.ListAsync()).Select(t => GetViewModel(t)));
        }

        [ProtectedApiEndpoint]
        [FunctionName("RekeyingTasks-Get")]
        public async Task<IActionResult> Get(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tasks/{taskId:guid}")] HttpRequest req,
            Guid taskId)
        {
            if (!req.IsValidUser()) return new UnauthorizedResult();

            if (!await RekeyingTasks.ContainsIdAsync(taskId))
            {
                await _eventDispatcher.DispatchEvent(AuthJanitorSystemEvents.AnomalousEventOccurred, nameof(AdminApi.RekeyingTasks.Get), "Rekeying Task not found");
                return new NotFoundResult();
            }

            return new OkObjectResult(GetViewModel((await RekeyingTasks.GetAsync(taskId))));
        }

        [ProtectedApiEndpoint]
        [FunctionName("RekeyingTasks-Delete")]
        public async Task<IActionResult> Delete(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "tasks/{taskId:guid}")] HttpRequest req,
            Guid taskId)
        {
            if (!req.IsValidUser(AuthJanitorRoles.ServiceOperator, AuthJanitorRoles.GlobalAdmin)) return new UnauthorizedResult();

            if (!await RekeyingTasks.ContainsIdAsync(taskId))
            {
                await _eventDispatcher.DispatchEvent(AuthJanitorSystemEvents.AnomalousEventOccurred, nameof(AdminApi.RekeyingTasks.Delete), "Rekeying Task not found");
                return new NotFoundResult();
            }

            await RekeyingTasks.DeleteAsync(taskId);

            await _eventDispatcher.DispatchEvent(AuthJanitorSystemEvents.RotationTaskDeleted, nameof(AdminApi.RekeyingTasks.Delete), taskId);

            return new OkResult();
        }

        [ProtectedApiEndpoint]
        [FunctionName("RekeyingTasks-Approve")]
        public async Task<IActionResult> Approve(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "tasks/{taskId:guid}/approve")] HttpRequest req,
            Guid taskId
        )
        {
            if (!req.IsValidUser(AuthJanitorRoles.ServiceOperator, AuthJanitorRoles.GlobalAdmin)) return new UnauthorizedResult();

            var toRekey = await RekeyingTasks.GetOneAsync(t => t.ObjectId == taskId);
            if (toRekey == null)
            {
                await _eventDispatcher.DispatchEvent(AuthJanitorSystemEvents.AnomalousEventOccurred, nameof(AdminApi.RekeyingTasks.Delete), "Rekeying Task not found");
                return new NotFoundResult();
            }
            if (!toRekey.ConfirmationType.UsesOBOTokens())
            {
                await _eventDispatcher.DispatchEvent(AuthJanitorSystemEvents.AnomalousEventOccurred, nameof(AdminApi.RekeyingTasks.Approve), "Rekeying Task does not support Administrator approval");
                return new BadRequestErrorMessageResult("Task does not support Administrator approval");
            }

            await _taskExecutionManager.ExecuteRekeyingTaskWorkflow(toRekey.ObjectId);
            return new OkResult();
        }
    }
}
