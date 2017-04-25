﻿#region Copyright
//
// DotNetNuke® - http://www.dnnsoftware.com
// Copyright (c) 2002-2017
// by DotNetNuke Corporation
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
// to permit persons to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial portions
// of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED
// TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
// CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using Dnn.ExportImport.Components.Common;
using Dnn.ExportImport.Components.Dto;
using Dnn.ExportImport.Components.Entities;
using Dnn.ExportImport.Components.Providers;
using Dnn.ExportImport.Dto.Portal;
using Dnn.ExportImport.Dto.Taxonomy;
using Dnn.ExportImport.Dto.Workflow;
using DotNetNuke.Common;
using DotNetNuke.Common.Utilities;
using DotNetNuke.Entities.Content.Workflow;
using DotNetNuke.Entities.Content.Workflow.Entities;
using DotNetNuke.Entities.Tabs;
using DotNetNuke.Entities.Users;
using DotNetNuke.Security.Permissions;
using DotNetNuke.Security.Roles;

namespace Dnn.ExportImport.Components.Services
{
    public class WorkflowsExportService : BasePortableService
    {
        public override string Category => Constants.Category_Workflows;

        public override string ParentCategory => Constants.Category_Portal;

        public override uint Priority => 6;

        public override int GetImportTotal()
        {
            return Repository.GetCount<TaxonomyVocabulary>() + Repository.GetCount<TaxonomyTerm>();
        }

        #region Exporting

        public override void ExportData(ExportImportJob exportJob, ExportDto exportDto)
        {
            if (CheckPoint.Stage > 0) return;
            if (CheckCancelled(exportJob)) return;

            var fromDate = (exportDto.FromDateUtc ?? Constants.MinDbTime).ToLocalTime();
            var toDate = exportDto.ToDateUtc.ToLocalTime();

            var contentWorkflows = GetWorkflows(exportDto.PortalId, exportDto.IncludeDeletions);
            if (contentWorkflows.Count > 0)
            {
                var defaultWorkflowId = TabWorkflowSettings.Instance.GetDefaultTabWorkflowId(exportDto.PortalId);
                var defaultWorkflow = contentWorkflows.FirstOrDefault(w => w.WorkflowID == defaultWorkflowId);
                if (defaultWorkflow != null)
                {
                    defaultWorkflow.IsDefault = true;
                }

                CheckPoint.TotalItems = contentWorkflows.Count;
                Repository.CreateItems(contentWorkflows);
                Result.AddLogEntry("Exported ContentWorkflows", contentWorkflows.Count.ToString());

                foreach (var workflow in contentWorkflows)
                {
                    var contentWorkflowStates = GetWorkflowStates(workflow.WorkflowID);
                    Repository.CreateItems(contentWorkflowStates, workflow.Id);

                    foreach (var workflowState in contentWorkflowStates)
                    {
                        var contentWorkflowStatePermissions = GetWorkflowStatePermissions(workflowState.StateID, toDate, fromDate);
                        Repository.CreateItems(contentWorkflowStatePermissions, workflowState.Id);
                    }
                }
            }

            CheckPoint.Progress = 100;
            CheckPoint.Completed = true;
            CheckPoint.Stage++;
            CheckPoint.StageData = null;
            CheckPointStageCallback(this);
        }

        private static List<ExportWorkflow> GetWorkflows(int portalId, bool includeDeletions)
        {
            return CBO.FillCollection<ExportWorkflow>(
                DataProvider.Instance().GetAllWorkflows(portalId, includeDeletions));
        }

        private static List<ExportWorkflowState> GetWorkflowStates(int workflowId)
        {
            return CBO.FillCollection<ExportWorkflowState>(
                DataProvider.Instance().GetAllWorkflowStates(workflowId));
        }

        private static List<ExportWorkflowStatePermission> GetWorkflowStatePermissions(
            int stateId, DateTime toDate, DateTime? fromDate)
        {
            return CBO.FillCollection<ExportWorkflowStatePermission>(
                DataProvider.Instance().GetAllWorkflowStatePermissions(stateId, toDate, fromDate));
        }

        #endregion

        #region Importing

        public override void ImportData(ExportImportJob importJob, ImportDto importDto)
        {
            if (CheckCancelled(importJob) || CheckPoint.Stage >= 1 || CheckPoint.Completed || CheckPointStageCallback(this))
            {
                return;
            }

            var workflowManager = WorkflowManager.Instance;
            var workflowStateManager = WorkflowStateManager.Instance;
            var portalId = importJob.PortalId;
            var importWorkflows = Repository.GetAllItems<ExportWorkflow>().ToList();
            var existWorkflows = workflowManager.GetWorkflows(portalId).ToList();
            var defaultTabWorkflowId = importWorkflows.FirstOrDefault(w => w.IsDefault)?.WorkflowID ?? 1;
            CheckPoint.TotalItems = CheckPoint.TotalItems <= 0 ? importWorkflows.Count : CheckPoint.TotalItems;

            foreach (var importWorkflow in importWorkflows)
            {
                var workflow = existWorkflows.FirstOrDefault(w => w.WorkflowName == importWorkflow.WorkflowName);
                if (workflow != null)
                {
                    if (!importWorkflow.IsSystem && importDto.CollisionResolution == CollisionResolution.Overwrite)
                    {
                        if (workflow.Description != importWorkflow.Description ||
                            workflow.WorkflowKey != importWorkflow.WorkflowKey)
                        {
                            workflow.Description = importWorkflow.Description;
                            workflow.WorkflowKey = importWorkflow.WorkflowKey;
                            workflowManager.UpdateWorkflow(workflow);
                            Result.AddLogEntry("Updated workflow", workflow.WorkflowName);
                        }
                    }
                }
                else
                {
                    workflow = new Workflow
                    {
                        PortalID = portalId,
                        WorkflowName = importWorkflow.WorkflowName,
                        Description = importWorkflow.Description,
                        WorkflowKey = importWorkflow.WorkflowKey,
                    };

                    workflowManager.AddWorkflow(workflow);
                    Result.AddLogEntry("Added workflow", workflow.WorkflowName);

                    if (importWorkflow.WorkflowID == defaultTabWorkflowId)
                    {
                        TabWorkflowSettings.Instance.SetDefaultTabWorkflowId(portalId, workflow.WorkflowID);
                    }
                }

                importWorkflow.LocalId = workflow.WorkflowID;

                var importStates = Repository.GetRelatedItems<ExportWorkflowState>(importWorkflow.Id).ToList();
                foreach (var importState in importStates)
                {
                    var workflowState = workflow.States.FirstOrDefault(s => s.StateName == importState.StateName);
                    if (workflowState != null)
                    {
                        if (!workflowState.IsSystem)
                        {
                            workflowState.Order = importState.Order;
                            workflowState.IsSystem = false;
                            workflowState.SendNotification = importState.SendNotification;
                            workflowState.SendNotificationToAdministrators = importState.SendNotificationToAdministrators;
                            workflowStateManager.UpdateWorkflowState(workflowState);
                            Result.AddLogEntry("Updated workflow state", workflowState.StateID.ToString());
                        }
                    }
                    else
                    {
                        workflowState = new WorkflowState
                        {
                            StateName = importState.StateName,
                            WorkflowID = workflow.WorkflowID,
                            Order = importState.Order,
                            IsSystem = importState.IsSystem,
                            SendNotification = importState.SendNotification,
                            SendNotificationToAdministrators = importState.SendNotificationToAdministrators
                        };
                        WorkflowStateManager.Instance.AddWorkflowState(workflowState);
                        Result.AddLogEntry("Added workflow state", workflowState.StateID.ToString());
                    }

                    importState.LocalId = workflowState.StateID;
                    var importPermissions = Repository.GetRelatedItems<ExportWorkflowStatePermission>(importState.Id);
                    foreach (var importPermission in importPermissions)
                    {
                        var permissionId = new PermissionController().GetPermissionByCodeAndKey(
                                importPermission.PermissionCode, importPermission.PermissionKey)
                            .OfType<PermissionInfo>().FirstOrDefault()?.PermissionID ?? -1;

                        if (permissionId > -1)
                        {
                            var importRoleId = importPermission.RoleID.GetValueOrDefault(Convert.ToInt32(Globals.glbRoleNothing));
                            var importUserId = importPermission.UserID.GetValueOrDefault(-1);

                            var roleFound = true;
                            var userFound = true;

                            if (importRoleId > -1)
                            {
                                var role = RoleController.Instance.GetRoleByName(portalId, importPermission.RoleName);
                                if (role == null)
                                {
                                    roleFound = false;
                                }
                                else
                                {
                                    importRoleId = role.RoleID;
                                }
                            }

                            if (importUserId > -1)
                            {
                                var user = UserController.GetUserByName(portalId, importPermission.Username);
                                if (user == null)
                                {
                                    userFound = false;
                                }
                                else
                                {
                                    importUserId = user.UserID;
                                }
                            }

                            if (roleFound || userFound)
                            {
                                var permission = new WorkflowStatePermission
                                {
                                    PermissionID = permissionId,
                                    StateID = workflowState.StateID,
                                    RoleID = importRoleId,
                                    UserID = importUserId,
                                    AllowAccess = importPermission.AllowAccess,
                                    //TODO: ModuleDefID = ??? what value to set here ?
                                };

                                WorkflowStateManager.Instance.AddWorkflowStatePermission(permission, -1);
                                importPermission.LocalId = permission.WorkflowStatePermissionID;
                                Result.AddLogEntry("Added workflow state permission", permission.WorkflowStatePermissionID.ToString());
                            }
                        }
                    }
                    //Repository.UpdateItems(importPermissions); // not necessary for now.
                }
                Repository.UpdateItems(importStates);

                Result.AddSummary("Imported Workflow", importWorkflows.Count.ToString());
                CheckPoint.ProcessedItems++;
                CheckPointStageCallback(this); // no need to return; very small amount of data processed
            }

            Repository.UpdateItems(importWorkflows);

            CheckPoint.Stage++;
            CheckPoint.StageData = null;
            CheckPoint.Progress = 100;
            CheckPoint.TotalItems = importWorkflows.Count;
            CheckPoint.ProcessedItems = importWorkflows.Count;
            CheckPointStageCallback(this);
        }

        #endregion
    }
}