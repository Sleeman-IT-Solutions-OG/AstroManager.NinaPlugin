using NINA.Core.Utility;
using Shared.Model.DTO.Client;
using Shared.Model.DTO.Scheduler;
using Shared.Model.DTO.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WpfBrushes = System.Windows.Media.Brushes;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Partial class containing all CRUD operations for:
    /// - Imaging Goals
    /// - Panel Custom Goals
    /// - Exposure Templates
    /// - Scheduler Configurations
    /// - Scheduler Target Templates
    /// - Moon Avoidance Profiles
    /// </summary>
    public partial class AstroManagerPlugin
    {
        #region Imaging Goal CRUD

        private async Task AddImagingGoalAsync()
        {
            if (_selectedTarget == null) return;
            
            if (string.IsNullOrEmpty(_settings.LicenseKey))
            {
                ConnectionStatus = "License key required to add imaging goals";
                ConnectionStatusColor = WpfBrushes.Red;
                return;
            }
            
            if (_exposureTemplates == null || !_exposureTemplates.Any())
            {
                var templates = await _apiClient.GetExposureTemplatesAsync();
                if (templates != null && templates.Any())
                {
                    _exposureTemplates = new ObservableCollection<ExposureTemplateDto>(templates);
                    RaisePropertyChanged(nameof(ExposureTemplates));
                    RaisePropertyChanged(nameof(ExposureTemplateCount));
                }
            }
            
            if (_exposureTemplates == null || !_exposureTemplates.Any())
            {
                ConnectionStatus = "No exposure templates - create templates first";
                ConnectionStatusColor = WpfBrushes.Orange;
                return;
            }
            
            var templateToUse = _selectedExposureTemplate ?? _exposureTemplates.First();
            
            var newGoal = new ImagingGoalDto
            {
                Id = Guid.NewGuid(),
                ScheduledTargetId = _selectedTarget.Id,
                ExposureTemplateId = templateToUse.Id,
                ExposureTemplate = templateToUse,
                GoalExposureCount = 20,
                IsEnabled = true
            };
            
            _selectedTarget.ImagingGoals.Add(newGoal);
            RaisePropertyChanged(nameof(SelectedTargetImagingGoals));
            RaisePropertyChanged(nameof(SelectedTargetGoalsCount));
            
            var goalToSelect = newGoal;
            
            if (!string.IsNullOrEmpty(_settings.LicenseKey))
            {
                ConnectionStatus = "Saving goal...";
                var result = await _apiClient.AddImagingGoalAsync(_selectedTarget.Id, newGoal);
                if (result != null)
                {
                    var idx = _selectedTarget.ImagingGoals.FindIndex(g => g.Id == newGoal.Id);
                    if (idx >= 0)
                    {
                        _selectedTarget.ImagingGoals[idx] = result;
                        goalToSelect = result;
                    }
                    ConnectionStatus = $"Goal added: {templateToUse.Name}";
                    ConnectionStatusColor = WpfBrushes.Green;
                    Logger.Info($"AstroManager: Added imaging goal {result.Id} to target {_selectedTarget.Name}");
                }
                else
                {
                    ConnectionStatus = "⚠️ No connection - goal saved locally only!";
                    ConnectionStatusColor = WpfBrushes.Orange;
                    Logger.Warning($"AstroManager: Failed to add imaging goal to API for target {_selectedTarget.Name}, saved locally only");
                }
            }
            
            _targetStore.UpdateTarget(_selectedTarget);
            RefreshTargetsList();
            RaisePropertyChanged(nameof(SelectedTargetImagingGoals));
            SelectedImagingGoal = goalToSelect;
        }

        private async Task SaveImagingGoalAsync()
        {
            if (_selectedTarget == null || _selectedImagingGoal == null) return;
            
            if (string.IsNullOrEmpty(_settings.LicenseKey))
            {
                ConnectionStatus = "License key required to save imaging goals";
                ConnectionStatusColor = WpfBrushes.Red;
                return;
            }
            
            _targetStore.UpdateTarget(_selectedTarget);
            
            var result = await _apiClient.UpdateImagingGoalAsync(_selectedTarget.Id, _selectedImagingGoal);
            if (result != null)
            {
                ConnectionStatus = "Imaging goal saved";
                ConnectionStatusColor = WpfBrushes.Green;
            }
            else
            {
                ConnectionStatus = "⚠️ No connection - saved locally only!";
                ConnectionStatusColor = WpfBrushes.Orange;
            }
            
            RaisePropertyChanged(nameof(SelectedTargetImagingGoals));
            
            _selectedImagingGoal = null;
            _selectedImagingGoalBackup = null;
            RaisePropertyChanged(nameof(SelectedImagingGoal));
            RaisePropertyChanged(nameof(HasSelectedImagingGoal));
        }

        private async Task DeleteImagingGoalAsync()
        {
            if (_selectedTarget == null || _imagingGoalToDelete == null) return;
            
            if (string.IsNullOrEmpty(_settings.LicenseKey))
            {
                ConnectionStatus = "License key required to delete imaging goals";
                ConnectionStatusColor = WpfBrushes.Red;
                return;
            }
            
            var goalId = _imagingGoalToDelete.Id;
            
            _selectedTarget.ImagingGoals.RemoveAll(g => g.Id == goalId);
            _targetStore.UpdateTarget(_selectedTarget);
            
            var success = await _apiClient.DeleteImagingGoalAsync(_selectedTarget.Id, goalId);
            ConnectionStatus = success ? "Imaging goal deleted" : "⚠️ No connection - deleted locally only!";
            ConnectionStatusColor = success ? WpfBrushes.Green : WpfBrushes.Orange;
            
            _imagingGoalToDelete = null;
            SelectedImagingGoal = null;
            RaisePropertyChanged(nameof(SelectedTargetImagingGoals));
            RaisePropertyChanged(nameof(SelectedTargetGoalsCount));
        }

        private void CancelImagingGoalEdit()
        {
            if (_selectedImagingGoal != null && _selectedImagingGoalBackup != null && _selectedTarget != null)
            {
                var index = _selectedTarget.ImagingGoals.FindIndex(g => g.Id == _selectedImagingGoal.Id);
                if (index >= 0)
                {
                    _selectedTarget.ImagingGoals[index].ExposureTemplateId = _selectedImagingGoalBackup.ExposureTemplateId;
                    _selectedTarget.ImagingGoals[index].ExposureTemplate = _selectedImagingGoalBackup.ExposureTemplate;
                    _selectedTarget.ImagingGoals[index].GoalExposureCount = _selectedImagingGoalBackup.GoalExposureCount;
                    _selectedTarget.ImagingGoals[index].IsEnabled = _selectedImagingGoalBackup.IsEnabled;
                }
            }
            _selectedImagingGoal = null;
            _selectedImagingGoalBackup = null;
            RaisePropertyChanged(nameof(SelectedImagingGoal));
            RaisePropertyChanged(nameof(HasSelectedImagingGoal));
            RaisePropertyChanged(nameof(SelectedTargetImagingGoals));
        }

        private void ConfirmDeleteImagingGoal()
        {
            if (_selectedImagingGoal == null) return;
            
            _imagingGoalToDelete = _selectedImagingGoal;
            var filterName = _selectedImagingGoal.Filter.ToString();
            ConfirmDialogTitle = "Delete Imaging Goal";
            ConfirmDialogMessage = $"Are you sure you want to delete the imaging goal for {filterName}?";
            _confirmDialogAction = async () => await DeleteImagingGoalAsync();
            ShowConfirmDialog = true;
        }

        #endregion

        #region Panel Custom Goal CRUD

        private async Task AddPanelCustomGoalAsync()
        {
            if (_selectedPanel == null || _selectedTarget == null) return;
            
            var defaultTemplate = ExposureTemplates.FirstOrDefault();
            if (defaultTemplate == null)
            {
                ConnectionStatus = "No exposure templates available";
                ConnectionStatusColor = WpfBrushes.Orange;
                return;
            }
            
            var newGoal = new PanelImagingGoalDto
            {
                Id = Guid.NewGuid(),
                ScheduledTargetPanelId = _selectedPanel.Id,
                ExposureTemplateId = defaultTemplate.Id,
                ExposureTemplate = defaultTemplate,
                GoalExposureCount = 10,
                CompletedExposures = 0,
                IsCustomGoal = true
            };
            
            _selectedPanel.ImagingGoals ??= new List<PanelImagingGoalDto>();
            _selectedPanel.ImagingGoals.Add(newGoal);
            
            SelectedPanelGoal = newGoal;
            
            RaisePropertyChanged(nameof(SelectedPanelCustomGoals));
            RaisePropertyChanged(nameof(SelectedPanelHasCustomGoals));
            
            Logger.Info($"AstroManager: Added new custom goal for panel {_selectedPanel.PanelName}");
        }

        private async Task SavePanelCustomGoalsAsync()
        {
            if (_selectedPanel == null || _selectedTarget == null) return;
            
            var customGoals = _selectedPanel.ImagingGoals?.Where(g => g.IsCustomGoal).ToList() ?? new List<PanelImagingGoalDto>();
            
            if (!string.IsNullOrEmpty(_settings.LicenseKey))
            {
                ConnectionStatus = "Saving panel goals...";
                var success = await _apiClient.UpdatePanelCustomGoalsAsync(_selectedPanel.Id, customGoals);
                
                if (success)
                {
                    ConnectionStatus = "Panel goals saved";
                    ConnectionStatusColor = WpfBrushes.Green;
                }
                else
                {
                    ConnectionStatus = "⚠️ No connection - panel goals saved locally only!";
                    ConnectionStatusColor = WpfBrushes.Orange;
                }
            }
            
            _selectedPanelGoal = null;
            _selectedPanelGoalBackup = null;
            RaisePropertyChanged(nameof(SelectedPanelGoal));
            RaisePropertyChanged(nameof(HasSelectedPanelGoal));
            
            RaisePropertyChanged(nameof(SelectedPanelCustomGoals));
            RaisePropertyChanged(nameof(SelectedPanelHasCustomGoals));
            RaisePropertyChanged(nameof(SelectedTargetPanels));
        }

        private async Task DeletePanelCustomGoalAsync()
        {
            if (_selectedPanel == null || _selectedPanelGoal == null) return;
            
            var goalId = _selectedPanelGoal.Id;
            
            _selectedPanel.ImagingGoals?.RemoveAll(g => g.Id == goalId);
            
            await SavePanelCustomGoalsAsync();
            
            SelectedPanelGoal = null;
            RaisePropertyChanged(nameof(SelectedPanelCustomGoals));
            RaisePropertyChanged(nameof(SelectedPanelHasCustomGoals));
            
            Logger.Info($"AstroManager: Deleted custom goal from panel {_selectedPanel.PanelName}");
        }
        
        private async Task SavePanelGoalAsync()
        {
            if (_selectedPanel == null || _selectedPanelGoal == null) return;
            
            var panelName = _selectedPanel.PanelName;
            
            await SavePanelCustomGoalsAsync();
            
            RaisePropertyChanged(nameof(SelectedPanelImagingGoals));
            RaisePropertyChanged(nameof(SelectedPanelBaseGoals));
            
            Logger.Info($"AstroManager: Saved panel goal for panel {panelName}");
        }
        
        private void CancelPanelGoalEdit()
        {
            if (_selectedPanelGoal != null && _selectedPanelGoalBackup != null && _selectedPanel != null)
            {
                var goal = _selectedPanel.ImagingGoals?.FirstOrDefault(g => g.Id == _selectedPanelGoal.Id);
                if (goal != null)
                {
                    goal.ExposureTemplateId = _selectedPanelGoalBackup.ExposureTemplateId;
                    goal.ExposureTemplate = _selectedPanelGoalBackup.ExposureTemplate;
                    goal.GoalExposureCount = _selectedPanelGoalBackup.GoalExposureCount;
                    goal.IsEnabled = _selectedPanelGoalBackup.IsEnabled;
                }
            }
            _selectedPanelGoal = null;
            _selectedPanelGoalBackup = null;
            RaisePropertyChanged(nameof(SelectedPanelGoal));
            RaisePropertyChanged(nameof(HasSelectedPanelGoal));
            RaisePropertyChanged(nameof(SelectedPanelCustomGoals));
            RaisePropertyChanged(nameof(SelectedPanelBaseGoals));
        }
        
        private async Task DeletePanelGoalAsync()
        {
            if (_selectedPanel == null || _selectedPanelGoal == null || !_selectedPanelGoal.IsCustomGoal) return;
            
            var goalId = _selectedPanelGoal.Id;
            
            _selectedPanel.ImagingGoals?.RemoveAll(g => g.Id == goalId);
            
            await SavePanelCustomGoalsAsync();
            
            SelectedPanelGoal = null;
            RaisePropertyChanged(nameof(SelectedPanelImagingGoals));
            RaisePropertyChanged(nameof(SelectedPanelCustomGoals));
            RaisePropertyChanged(nameof(SelectedPanelHasCustomGoals));
            
            Logger.Info($"AstroManager: Deleted custom goal from panel {_selectedPanel.PanelName}");
        }

        #endregion

        #region Exposure Template CRUD

        private async Task AddExposureTemplateAsync()
        {
            var newTemplate = new CreateExposureTemplateDto
            {
                Name = "New Template",
                Filter = Shared.Model.DTO.Settings.ECameraFilter.L,
                ExposureTimeSeconds = 300,
                Binning = 1,
                Gain = -1,
                Offset = -1,
                DefaultFilterPriority = 1
            };
            
            var result = await _apiClient.CreateExposureTemplateAsync(newTemplate);
            if (result != null)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    ExposureTemplates.Add(result);
                });
                SelectedExposureTemplate = result;
                ConnectionStatus = "Exposure template created";
                ConnectionStatusColor = WpfBrushes.Green;
                RaisePropertyChanged(nameof(ExposureTemplateCount));
            }
            else
            {
                ConnectionStatus = "Failed to create template";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }

        private void CancelExposureTemplateEdit()
        {
            _isRefreshingExposureTemplateGrid = true;
            
            if (_selectedExposureTemplate != null && _selectedExposureTemplateBackup != null)
            {
                var index = ExposureTemplates.IndexOf(_selectedExposureTemplate);
                if (index >= 0)
                {
                    var restored = new ExposureTemplateDto
                    {
                        Id = _selectedExposureTemplateBackup.Id,
                        Name = _selectedExposureTemplateBackup.Name,
                        Filter = _selectedExposureTemplateBackup.Filter,
                        ExposureTimeSeconds = _selectedExposureTemplateBackup.ExposureTimeSeconds,
                        Binning = _selectedExposureTemplateBackup.Binning,
                        Gain = _selectedExposureTemplateBackup.Gain,
                        Offset = _selectedExposureTemplateBackup.Offset,
                        DefaultFilterPriority = _selectedExposureTemplateBackup.DefaultFilterPriority,
                        ReadoutMode = _selectedExposureTemplateBackup.ReadoutMode,
                        DitherEveryX = _selectedExposureTemplateBackup.DitherEveryX,
                        MinAltitude = _selectedExposureTemplateBackup.MinAltitude,
                        AcceptableTwilight = _selectedExposureTemplateBackup.AcceptableTwilight,
                        MoonAvoidanceProfileId = _selectedExposureTemplateBackup.MoonAvoidanceProfileId,
                        MoonAvoidanceProfileName = _selectedExposureTemplateBackup.MoonAvoidanceProfileName,
                        IsActive = _selectedExposureTemplateBackup.IsActive
                    };
                    ExposureTemplates[index] = restored;
                }
            }
            _selectedExposureTemplate = null;
            _selectedExposureTemplateBackup = null;
            _isRefreshingExposureTemplateGrid = false;
            RaisePropertyChanged(nameof(SelectedExposureTemplate));
            RaisePropertyChanged(nameof(HasSelectedExposureTemplate));
        }

        private async Task SaveExposureTemplateAsync()
        {
            if (_selectedExposureTemplate == null) return;
            
            var updateDto = new UpdateExposureTemplateDto
            {
                Name = _selectedExposureTemplate.Name,
                Filter = _selectedExposureTemplate.Filter,
                ExposureTimeSeconds = _selectedExposureTemplate.ExposureTimeSeconds,
                Binning = _selectedExposureTemplate.Binning,
                Gain = _selectedExposureTemplate.Gain,
                Offset = _selectedExposureTemplate.Offset,
                MoonAvoidanceProfileId = _selectedExposureTemplate.MoonAvoidanceProfileId,
                DefaultFilterPriority = _selectedExposureTemplate.DefaultFilterPriority,
                AcceptableTwilight = _selectedExposureTemplate.AcceptableTwilight,
                ReadoutMode = _selectedExposureTemplate.ReadoutMode,
                DitherEveryX = _selectedExposureTemplate.DitherEveryX,
                MinAltitude = _selectedExposureTemplate.MinAltitude,
                IsActive = _selectedExposureTemplate.IsActive,
                ObservatoryId = _selectedExposureTemplate.ObservatoryId,
                EquipmentId = _selectedExposureTemplate.EquipmentId,
                LastKnownUpdatedAt = _selectedExposureTemplate.UpdatedAt
            };
            
            var (result, conflictDetected) = await _apiClient.UpdateExposureTemplateWithConflictCheckAsync(_selectedExposureTemplate.Id, updateDto);
            if (conflictDetected)
            {
                ConnectionStatus = "⚠️ Conflict: Template was modified elsewhere. Please reload.";
                ConnectionStatusColor = WpfBrushes.Orange;
                ConfirmDialogTitle = "Data Conflict Detected";
                ConfirmDialogMessage = "This template was modified in AstroManager while you were editing.\n\nWould you like to reload the latest data? Your changes will be lost.";
                _confirmDialogAction = async () => await LoadSettingsFromApiAsync();
                ShowConfirmDialog = true;
            }
            else if (result != null)
            {
                _selectedExposureTemplate.UpdatedAt = result.UpdatedAt;
                _selectedExposureTemplate.MoonAvoidanceProfileName = result.MoonAvoidanceProfileName;
                _selectedExposureTemplateBackup = null;
                ConnectionStatus = "Exposure template saved";
                ConnectionStatusColor = WpfBrushes.Green;
                SelectedExposureTemplate = null;
            }
            else
            {
                ConnectionStatus = "Failed to save template";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }

        private async Task DeleteExposureTemplateAsync(ExposureTemplateDto? template)
        {
            var templateToDelete = template ?? _selectedExposureTemplate;
            if (templateToDelete == null) return;
            
            ConfirmDialogTitle = "Delete Template";
            ConfirmDialogMessage = $"Are you sure you want to delete '{templateToDelete.Name}'?";
            _confirmDialogAction = async () => await ExecuteDeleteExposureTemplateAsync(templateToDelete);
            ShowConfirmDialog = true;
        }
        
        private async Task ExecuteDeleteExposureTemplateAsync(ExposureTemplateDto templateToDelete, bool force = false)
        {
            var (success, inUseCount) = await _apiClient.DeleteExposureTemplateAsync(templateToDelete.Id, force);
            if (success)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    ExposureTemplates.Remove(templateToDelete);
                });
                if (_selectedExposureTemplate?.Id == templateToDelete.Id)
                    SelectedExposureTemplate = null;
                ConnectionStatus = "Exposure template deleted";
                ConnectionStatusColor = WpfBrushes.Green;
                RaisePropertyChanged(nameof(ExposureTemplateCount));
            }
            else if (inUseCount > 0)
            {
                ConfirmDialogTitle = "Template In Use";
                ConfirmDialogMessage = $"'{templateToDelete.Name}' is used by {inUseCount} imaging goal(s).\n\nDeleting it will leave those goals without an exposure template.\n\nDelete anyway?";
                _confirmDialogAction = async () => await ExecuteDeleteExposureTemplateAsync(templateToDelete, force: true);
                ShowConfirmDialog = true;
            }
            else
            {
                ConnectionStatus = "Failed to delete template";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }

        private async Task CopyExposureTemplateAsync(ExposureTemplateDto? template)
        {
            var templateToCopy = template ?? _selectedExposureTemplate;
            if (templateToCopy == null) return;
            
            var copyDto = new CreateExposureTemplateDto
            {
                Name = $"{templateToCopy.Name} (Copy)",
                Filter = templateToCopy.Filter,
                ExposureTimeSeconds = templateToCopy.ExposureTimeSeconds,
                Binning = templateToCopy.Binning,
                Gain = templateToCopy.Gain,
                Offset = templateToCopy.Offset,
                DefaultFilterPriority = templateToCopy.DefaultFilterPriority,
                AcceptableTwilight = templateToCopy.AcceptableTwilight,
                ReadoutMode = templateToCopy.ReadoutMode,
                DitherEveryX = templateToCopy.DitherEveryX,
                MinAltitude = templateToCopy.MinAltitude,
                MoonAvoidanceProfileId = templateToCopy.MoonAvoidanceProfileId,
                EquipmentId = templateToCopy.EquipmentId
            };
            
            var result = await _apiClient.CreateExposureTemplateAsync(copyDto);
            if (result != null)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    ExposureTemplates.Add(result);
                });
                SelectedExposureTemplate = result;
                ConnectionStatus = "Template copied";
                ConnectionStatusColor = WpfBrushes.Green;
                RaisePropertyChanged(nameof(ExposureTemplateCount));
            }
            else
            {
                ConnectionStatus = "Failed to copy template";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }

        #endregion

        #region Cancel Edit Methods

        private void CancelSchedulerConfigEdit()
        {
            if (_selectedSchedulerConfiguration != null && _selectedSchedulerConfigurationBackup != null)
            {
                var index = SchedulerConfigurations.IndexOf(_selectedSchedulerConfiguration);
                if (index >= 0)
                {
                    var restored = CloneSchedulerConfigurationDto(_selectedSchedulerConfigurationBackup);
                    SchedulerConfigurations[index] = restored;
                }
            }
            _selectedSchedulerConfiguration = null;
            _selectedSchedulerConfigurationBackup = null;
            RaisePropertyChanged(nameof(SelectedSchedulerConfiguration));
            RaisePropertyChanged(nameof(HasSelectedSchedulerConfiguration));
        }

        private void CancelSchedulerTargetTemplateEdit()
        {
            if (_selectedSchedulerTargetTemplate != null && _selectedSchedulerTargetTemplateBackup != null)
            {
                var index = SchedulerTargetTemplates.IndexOf(_selectedSchedulerTargetTemplate);
                if (index >= 0)
                {
                    var restored = new SchedulerTargetTemplateDto
                    {
                        Id = _selectedSchedulerTargetTemplateBackup.Id,
                        Name = _selectedSchedulerTargetTemplateBackup.Name,
                        Description = _selectedSchedulerTargetTemplateBackup.Description,
                        EquipmentId = _selectedSchedulerTargetTemplateBackup.EquipmentId,
                        FilterShootingPattern = _selectedSchedulerTargetTemplateBackup.FilterShootingPattern,
                        FilterBatchSize = _selectedSchedulerTargetTemplateBackup.FilterBatchSize,
                        MinSessionDurationMinutes = _selectedSchedulerTargetTemplateBackup.MinSessionDurationMinutes,
                        MinAltitude = _selectedSchedulerTargetTemplateBackup.MinAltitude,
                        MaxHoursPerNight = _selectedSchedulerTargetTemplateBackup.MaxHoursPerNight,
                        MaxSequenceTimeMinutes = _selectedSchedulerTargetTemplateBackup.MaxSequenceTimeMinutes,
                        GoalCompletionBehaviour = _selectedSchedulerTargetTemplateBackup.GoalCompletionBehaviour,
                        LowerPriorityTo = _selectedSchedulerTargetTemplateBackup.LowerPriorityTo,
                        UseMoonAvoidance = _selectedSchedulerTargetTemplateBackup.UseMoonAvoidance,
                        MoonAvoidanceProfilesJson = _selectedSchedulerTargetTemplateBackup.MoonAvoidanceProfilesJson,
                        MinStartTime = _selectedSchedulerTargetTemplateBackup.MinStartTime,
                        MaxStartTime = _selectedSchedulerTargetTemplateBackup.MaxStartTime,
                        MinMoonPhasePercent = _selectedSchedulerTargetTemplateBackup.MinMoonPhasePercent,
                        MaxMoonPhasePercent = _selectedSchedulerTargetTemplateBackup.MaxMoonPhasePercent,
                        DisplayOrder = _selectedSchedulerTargetTemplateBackup.DisplayOrder
                    };
                    SchedulerTargetTemplates[index] = restored;
                }
            }
            _selectedSchedulerTargetTemplate = null;
            _selectedSchedulerTargetTemplateBackup = null;
            RaisePropertyChanged(nameof(SelectedSchedulerTargetTemplate));
            RaisePropertyChanged(nameof(HasSelectedSchedulerTargetTemplate));
        }

        private void CancelMoonAvoidanceProfileEdit()
        {
            if (_selectedMoonAvoidanceProfile != null && _selectedMoonAvoidanceProfileBackup != null)
            {
                var index = MoonAvoidanceProfiles.IndexOf(_selectedMoonAvoidanceProfile);
                if (index >= 0)
                {
                    var restored = new MoonAvoidanceProfileDto
                    {
                        Id = _selectedMoonAvoidanceProfileBackup.Id,
                        Name = _selectedMoonAvoidanceProfileBackup.Name,
                        FullMoonDistanceDegrees = _selectedMoonAvoidanceProfileBackup.FullMoonDistanceDegrees,
                        WidthInDays = _selectedMoonAvoidanceProfileBackup.WidthInDays,
                        MinMoonAltitudeDegrees = _selectedMoonAvoidanceProfileBackup.MinMoonAltitudeDegrees,
                        IsSystemDefault = _selectedMoonAvoidanceProfileBackup.IsSystemDefault
                    };
                    MoonAvoidanceProfiles[index] = restored;
                }
            }
            _selectedMoonAvoidanceProfile = null;
            _selectedMoonAvoidanceProfileBackup = null;
            RaisePropertyChanged(nameof(SelectedMoonAvoidanceProfile));
            RaisePropertyChanged(nameof(HasSelectedMoonAvoidanceProfile));
        }

        #endregion

        #region Unsaved Changes Detection

        private bool HasUnsavedExposureTemplateChanges()
        {
            if (_selectedExposureTemplate == null || _selectedExposureTemplateBackup == null) return false;
            return _selectedExposureTemplate.Name != _selectedExposureTemplateBackup.Name ||
                   _selectedExposureTemplate.Filter != _selectedExposureTemplateBackup.Filter ||
                   _selectedExposureTemplate.ExposureTimeSeconds != _selectedExposureTemplateBackup.ExposureTimeSeconds ||
                   _selectedExposureTemplate.Binning != _selectedExposureTemplateBackup.Binning ||
                   _selectedExposureTemplate.Gain != _selectedExposureTemplateBackup.Gain ||
                   _selectedExposureTemplate.Offset != _selectedExposureTemplateBackup.Offset ||
                   _selectedExposureTemplate.DefaultFilterPriority != _selectedExposureTemplateBackup.DefaultFilterPriority ||
                   _selectedExposureTemplate.ReadoutMode != _selectedExposureTemplateBackup.ReadoutMode ||
                   _selectedExposureTemplate.DitherEveryX != _selectedExposureTemplateBackup.DitherEveryX ||
                   _selectedExposureTemplate.MinAltitude != _selectedExposureTemplateBackup.MinAltitude ||
                   _selectedExposureTemplate.AcceptableTwilight != _selectedExposureTemplateBackup.AcceptableTwilight ||
                   _selectedExposureTemplate.MoonAvoidanceProfileId != _selectedExposureTemplateBackup.MoonAvoidanceProfileId;
        }

        private bool HasUnsavedSchedulerConfigChanges()
        {
            if (_selectedSchedulerConfiguration == null || _selectedSchedulerConfigurationBackup == null) return false;
            var currentJson = System.Text.Json.JsonSerializer.Serialize(_selectedSchedulerConfiguration);
            var backupJson = System.Text.Json.JsonSerializer.Serialize(_selectedSchedulerConfigurationBackup);
            return !string.Equals(currentJson, backupJson, StringComparison.Ordinal);
        }

        private bool HasUnsavedMoonAvoidanceProfileChanges()
        {
            if (_selectedMoonAvoidanceProfile == null || _selectedMoonAvoidanceProfileBackup == null) return false;
            if (_selectedMoonAvoidanceProfile.IsSystemDefault) return false;
            return _selectedMoonAvoidanceProfile.Name != _selectedMoonAvoidanceProfileBackup.Name ||
                   _selectedMoonAvoidanceProfile.FullMoonDistanceDegrees != _selectedMoonAvoidanceProfileBackup.FullMoonDistanceDegrees ||
                   _selectedMoonAvoidanceProfile.WidthInDays != _selectedMoonAvoidanceProfileBackup.WidthInDays ||
                   _selectedMoonAvoidanceProfile.MinMoonAltitudeDegrees != _selectedMoonAvoidanceProfileBackup.MinMoonAltitudeDegrees;
        }

        private bool HasUnsavedImagingGoalChanges()
        {
            if (_selectedImagingGoal == null || _selectedImagingGoalBackup == null) return false;
            return _selectedImagingGoal.ExposureTemplateId != _selectedImagingGoalBackup.ExposureTemplateId ||
                   _selectedImagingGoal.GoalExposureCount != _selectedImagingGoalBackup.GoalExposureCount ||
                   _selectedImagingGoal.IsEnabled != _selectedImagingGoalBackup.IsEnabled;
        }

        private bool HasUnsavedPanelGoalChanges()
        {
            if (_selectedPanelGoal == null || _selectedPanelGoalBackup == null) return false;
            return _selectedPanelGoal.ExposureTemplateId != _selectedPanelGoalBackup.ExposureTemplateId ||
                   _selectedPanelGoal.GoalExposureCount != _selectedPanelGoalBackup.GoalExposureCount ||
                   _selectedPanelGoal.IsEnabled != _selectedPanelGoalBackup.IsEnabled;
        }

        private bool HasUnsavedSchedulerTargetTemplateChanges()
        {
            if (_selectedSchedulerTargetTemplate == null || _selectedSchedulerTargetTemplateBackup == null) return false;
            return _selectedSchedulerTargetTemplate.Name != _selectedSchedulerTargetTemplateBackup.Name ||
                   _selectedSchedulerTargetTemplate.Description != _selectedSchedulerTargetTemplateBackup.Description ||
                   _selectedSchedulerTargetTemplate.MinAltitude != _selectedSchedulerTargetTemplateBackup.MinAltitude ||
                   _selectedSchedulerTargetTemplate.MaxHoursPerNight != _selectedSchedulerTargetTemplateBackup.MaxHoursPerNight ||
                   _selectedSchedulerTargetTemplate.MinSessionDurationMinutes != _selectedSchedulerTargetTemplateBackup.MinSessionDurationMinutes ||
                   _selectedSchedulerTargetTemplate.MaxSequenceTimeMinutes != _selectedSchedulerTargetTemplateBackup.MaxSequenceTimeMinutes ||
                   _selectedSchedulerTargetTemplate.GoalCompletionBehaviour != _selectedSchedulerTargetTemplateBackup.GoalCompletionBehaviour ||
                   _selectedSchedulerTargetTemplate.LowerPriorityTo != _selectedSchedulerTargetTemplateBackup.LowerPriorityTo ||
                   _selectedSchedulerTargetTemplate.UseMoonAvoidance != _selectedSchedulerTargetTemplateBackup.UseMoonAvoidance ||
                   _selectedSchedulerTargetTemplate.FilterShootingPattern != _selectedSchedulerTargetTemplateBackup.FilterShootingPattern ||
                   _selectedSchedulerTargetTemplate.MinStartTime != _selectedSchedulerTargetTemplateBackup.MinStartTime ||
                   _selectedSchedulerTargetTemplate.MaxStartTime != _selectedSchedulerTargetTemplateBackup.MaxStartTime ||
                   _selectedSchedulerTargetTemplate.MinMoonPhasePercent != _selectedSchedulerTargetTemplateBackup.MinMoonPhasePercent ||
                   _selectedSchedulerTargetTemplate.MaxMoonPhasePercent != _selectedSchedulerTargetTemplateBackup.MaxMoonPhasePercent;
        }

        private bool HasUnsavedTargetChanges()
        {
            if (_selectedTarget == null || _selectedTargetBackup == null) return false;
            return _selectedTarget.Name != _selectedTargetBackup.Name ||
                   _selectedTarget.RightAscension != _selectedTargetBackup.RightAscension ||
                   _selectedTarget.Declination != _selectedTargetBackup.Declination ||
                   _selectedTarget.Priority != _selectedTargetBackup.Priority ||
                   _selectedTarget.RepeatCount != _selectedTargetBackup.RepeatCount ||
                   _selectedTarget.Status != _selectedTargetBackup.Status ||
                   _selectedTarget.SchedulerTargetTemplateId != _selectedTargetBackup.SchedulerTargetTemplateId ||
                   _selectedTarget.Description != _selectedTargetBackup.Description ||
                   _selectedTarget.PA != _selectedTargetBackup.PA ||
                   _selectedTarget.Notes != _selectedTargetBackup.Notes ||
                   _selectedTarget.IsMosaic != _selectedTargetBackup.IsMosaic ||
                   _selectedTarget.MosaicPanelsX != _selectedTargetBackup.MosaicPanelsX ||
                   _selectedTarget.MosaicPanelsY != _selectedTargetBackup.MosaicPanelsY ||
                   _selectedTarget.MosaicOverlapPercent != _selectedTargetBackup.MosaicOverlapPercent ||
                   _selectedTarget.MosaicUseRotator != _selectedTargetBackup.MosaicUseRotator ||
                   _selectedTarget.UseCustomPanelGoals != _selectedTargetBackup.UseCustomPanelGoals ||
                   _selectedTarget.MosaicShootingStrategy != _selectedTargetBackup.MosaicShootingStrategy ||
                   _selectedTarget.MosaicPanelOrderingMethod != _selectedTargetBackup.MosaicPanelOrderingMethod ||
                   _selectedTarget.GoalOrderingMethod != _selectedTargetBackup.GoalOrderingMethod;
        }

        private void ShowDiscardChangesDialog(string itemType, Action onDiscard)
        {
            ConfirmDialogTitle = "Unsaved Changes";
            ConfirmDialogMessage = $"You have unsaved changes to the current {itemType}.\n\nDiscard changes and select new item?";
            _confirmDialogAction = onDiscard;
            _confirmDialogNoAction = null;
            ShowConfirmDialog = true;
        }

        #endregion

        #region Scheduler Configuration CRUD

        private async Task SaveSchedulerConfigAsync()
        {
            if (_selectedSchedulerConfiguration == null) return;
            
            var result = await _apiClient.UpdateSchedulerConfigurationAsync(_selectedSchedulerConfiguration);
            if (result != null)
            {
                _selectedSchedulerConfigurationBackup = null;
                ConnectionStatus = "Scheduler config saved";
                ConnectionStatusColor = WpfBrushes.Green;
                SelectedSchedulerConfiguration = null;
            }
            else
            {
                ConnectionStatus = "Failed to save config";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }
        
        private async Task AddSchedulerConfigAsync()
        {
            var newConfig = new SchedulerConfigurationDto
            {
                Name = "New Configuration",
                StartDate = DateTime.Today,
                EndDate = DateTime.Today.AddYears(1),
                PrimaryStrategy = Shared.Model.Enums.TargetSelectionStrategy.PriorityFirst,
                PrioritizationMode = Shared.Model.Enums.PrioritizationMode.Simple,
                MinAltitudeDegrees = 30,
                ImagingEfficiencyPercent = 75,
                FilterShootingPattern = "Loop",
                GoalCompletionBehavior = "Stop",
                AlwaysStopWhenNoTargetsForNight = true,
                EnableSafetyMonitorCheck = true,
                EnableMountAltitudeCheck = true,
                MinMountAltitudeDegrees = 20,
                ViolationAction = Shared.Model.Enums.SchedulerViolationAction.StopScheduler,
                ViolationRetryMinutes = 10
            };
            EnsureSchedulerConfigurationWeightedCriteriaInitialized(newConfig);
            
            var result = await _apiClient.CreateSchedulerConfigurationAsync(newConfig);
            if (result != null)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    SchedulerConfigurations.Add(result);
                });
                SelectedSchedulerConfiguration = result;
                ConnectionStatus = "Scheduler config created";
                ConnectionStatusColor = WpfBrushes.Green;
                RaisePropertyChanged(nameof(SchedulerConfigCount));
            }
            else
            {
                ConnectionStatus = "Failed to create config";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }
        
        private async Task DeleteSchedulerConfigAsync(SchedulerConfigurationDto? config)
        {
            var configToDelete = config ?? _selectedSchedulerConfiguration;
            if (configToDelete == null) return;
            
            ConfirmDialogTitle = "Delete Configuration";
            ConfirmDialogMessage = $"Are you sure you want to delete '{configToDelete.Name}'?";
            _confirmDialogAction = async () => await ExecuteDeleteSchedulerConfigAsync(configToDelete);
            ShowConfirmDialog = true;
        }
        
        private async Task ExecuteDeleteSchedulerConfigAsync(SchedulerConfigurationDto configToDelete)
        {
            var success = await _apiClient.DeleteSchedulerConfigurationAsync(configToDelete.Id);
            if (success)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    SchedulerConfigurations.Remove(configToDelete);
                });
                if (_selectedSchedulerConfiguration?.Id == configToDelete.Id)
                    SelectedSchedulerConfiguration = null;
                ConnectionStatus = "Scheduler config deleted";
                ConnectionStatusColor = WpfBrushes.Green;
                RaisePropertyChanged(nameof(SchedulerConfigCount));
            }
            else
            {
                ConnectionStatus = "Failed to delete config";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }
        
        private async Task CopySchedulerConfigAsync(SchedulerConfigurationDto? config)
        {
            var configToCopy = config ?? _selectedSchedulerConfiguration;
            if (configToCopy == null) return;
            
            var copyDto = CloneSchedulerConfigurationDto(configToCopy);
            copyDto.Id = Guid.Empty;
            copyDto.Name = $"{configToCopy.Name} (Copy)";
            copyDto.IsDefault = false;
            EnsureSchedulerConfigurationWeightedCriteriaInitialized(copyDto);
            
            var result = await _apiClient.CreateSchedulerConfigurationAsync(copyDto);
            if (result != null)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    SchedulerConfigurations.Add(result);
                });
                SelectedSchedulerConfiguration = result;
                ConnectionStatus = "Scheduler config copied";
                ConnectionStatusColor = WpfBrushes.Green;
                RaisePropertyChanged(nameof(SchedulerConfigCount));
            }
            else
            {
                ConnectionStatus = "Failed to copy config";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }
        
        private async Task SetDefaultSchedulerConfigAsync(SchedulerConfigurationDto? config)
        {
            var configToSet = config ?? _selectedSchedulerConfiguration;
            if (configToSet == null) return;
            
            var success = await _apiClient.SetDefaultSchedulerConfigurationAsync(configToSet.Id);
            if (success)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    foreach (var c in SchedulerConfigurations)
                    {
                        c.IsDefault = c.Id == configToSet.Id;
                    }
                    var updated = SchedulerConfigurations.ToList();
                    SchedulerConfigurations.Clear();
                    foreach (var c in updated)
                    {
                        SchedulerConfigurations.Add(c);
                    }
                });
                SchedulerConfig = configToSet;
                RaisePropertyChanged(nameof(SchedulerConfigurations));
                ConnectionStatus = $"'{configToSet.Name}' set as default";
                ConnectionStatusColor = WpfBrushes.Green;
            }
            else
            {
                ConnectionStatus = "Failed to set default config";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }

        #endregion
        
        #region Scheduler Target Template CRUD
        
        private async Task AddSchedulerTargetTemplateAsync()
        {
            var newTemplate = new CreateSchedulerTargetTemplateDto
            {
                Name = "New Template",
                MinAltitude = 30,
                GoalCompletionBehaviour = "Continue"
            };
            
            var result = await _apiClient.CreateSchedulerTargetTemplateAsync(newTemplate);
            if (result != null)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    SchedulerTargetTemplates.Add(result);
                });
                SelectedSchedulerTargetTemplate = result;
                ConnectionStatus = "Template created";
                ConnectionStatusColor = WpfBrushes.Green;
                RaisePropertyChanged(nameof(SchedulerTargetTemplateCount));
            }
            else
            {
                ConnectionStatus = "Failed to create template";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }
        
        private async Task SaveSchedulerTargetTemplateAsync()
        {
            if (_selectedSchedulerTargetTemplate == null) return;
            
            var updateDto = new UpdateSchedulerTargetTemplateDto
            {
                Id = _selectedSchedulerTargetTemplate.Id,
                Name = _selectedSchedulerTargetTemplate.Name,
                Description = _selectedSchedulerTargetTemplate.Description,
                EquipmentId = _selectedSchedulerTargetTemplate.EquipmentId,
                FilterShootingPattern = _selectedSchedulerTargetTemplate.FilterShootingPattern,
                FilterBatchSize = _selectedSchedulerTargetTemplate.FilterBatchSize,
                MinSessionDurationMinutes = _selectedSchedulerTargetTemplate.MinSessionDurationMinutes,
                MinAltitude = _selectedSchedulerTargetTemplate.MinAltitude,
                MaxHoursPerNight = _selectedSchedulerTargetTemplate.MaxHoursPerNight,
                MaxSequenceTimeMinutes = _selectedSchedulerTargetTemplate.MaxSequenceTimeMinutes,
                GoalCompletionBehaviour = _selectedSchedulerTargetTemplate.GoalCompletionBehaviour,
                LowerPriorityTo = _selectedSchedulerTargetTemplate.LowerPriorityTo,
                UseMoonAvoidance = _selectedSchedulerTargetTemplate.UseMoonAvoidance,
                MoonAvoidanceProfilesJson = _selectedSchedulerTargetTemplate.MoonAvoidanceProfilesJson,
                MinStartTime = _selectedSchedulerTargetTemplate.MinStartTime,
                MaxStartTime = _selectedSchedulerTargetTemplate.MaxStartTime,
                MinMoonPhasePercent = _selectedSchedulerTargetTemplate.MinMoonPhasePercent,
                MaxMoonPhasePercent = _selectedSchedulerTargetTemplate.MaxMoonPhasePercent,
                DisplayOrder = _selectedSchedulerTargetTemplate.DisplayOrder
            };
            
            var result = await _apiClient.UpdateSchedulerTargetTemplateAsync(_selectedSchedulerTargetTemplate.Id, updateDto);
            if (result != null)
            {
                _selectedSchedulerTargetTemplateBackup = new SchedulerTargetTemplateDto
                {
                    Id = result.Id,
                    Name = result.Name,
                    Description = result.Description,
                    EquipmentId = result.EquipmentId,
                    FilterShootingPattern = result.FilterShootingPattern,
                    FilterBatchSize = result.FilterBatchSize,
                    MinSessionDurationMinutes = result.MinSessionDurationMinutes,
                    MinAltitude = result.MinAltitude,
                    MaxHoursPerNight = result.MaxHoursPerNight,
                    MaxSequenceTimeMinutes = result.MaxSequenceTimeMinutes,
                    GoalCompletionBehaviour = result.GoalCompletionBehaviour,
                    LowerPriorityTo = result.LowerPriorityTo,
                    UseMoonAvoidance = result.UseMoonAvoidance,
                    MoonAvoidanceProfilesJson = result.MoonAvoidanceProfilesJson,
                    MinStartTime = result.MinStartTime,
                    MaxStartTime = result.MaxStartTime,
                    MinMoonPhasePercent = result.MinMoonPhasePercent,
                    MaxMoonPhasePercent = result.MaxMoonPhasePercent,
                    DisplayOrder = result.DisplayOrder
                };
                ConnectionStatus = "Template saved";
                ConnectionStatusColor = WpfBrushes.Green;
            }
            else
            {
                ConnectionStatus = "Failed to save template";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }
        
        private async Task DeleteSchedulerTargetTemplateAsync(SchedulerTargetTemplateDto? template)
        {
            var templateToDelete = template ?? _selectedSchedulerTargetTemplate;
            if (templateToDelete == null) return;
            
            ConfirmDialogTitle = "Delete Template";
            ConfirmDialogMessage = $"Are you sure you want to delete '{templateToDelete.Name}'?";
            _confirmDialogAction = async () => await ExecuteDeleteSchedulerTargetTemplateAsync(templateToDelete);
            ShowConfirmDialog = true;
        }
        
        private async Task ExecuteDeleteSchedulerTargetTemplateAsync(SchedulerTargetTemplateDto templateToDelete)
        {
            var success = await _apiClient.DeleteSchedulerTargetTemplateAsync(templateToDelete.Id);
            if (success)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    SchedulerTargetTemplates.Remove(templateToDelete);
                });
                if (_selectedSchedulerTargetTemplate?.Id == templateToDelete.Id)
                    SelectedSchedulerTargetTemplate = null;
                ConnectionStatus = "Template deleted";
                ConnectionStatusColor = WpfBrushes.Green;
                RaisePropertyChanged(nameof(SchedulerTargetTemplateCount));
            }
            else
            {
                ConnectionStatus = "Failed to delete template";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }
        
        private async Task CopySchedulerTargetTemplateAsync(SchedulerTargetTemplateDto? template)
        {
            var templateToCopy = template ?? _selectedSchedulerTargetTemplate;
            if (templateToCopy == null) return;
            
            var copyDto = new CreateSchedulerTargetTemplateDto
            {
                Name = $"{templateToCopy.Name} (Copy)",
                Description = templateToCopy.Description,
                EquipmentId = templateToCopy.EquipmentId,
                FilterShootingPattern = templateToCopy.FilterShootingPattern,
                FilterBatchSize = templateToCopy.FilterBatchSize,
                MinSessionDurationMinutes = templateToCopy.MinSessionDurationMinutes,
                MinAltitude = templateToCopy.MinAltitude,
                MaxHoursPerNight = templateToCopy.MaxHoursPerNight,
                MaxSequenceTimeMinutes = templateToCopy.MaxSequenceTimeMinutes,
                GoalCompletionBehaviour = templateToCopy.GoalCompletionBehaviour,
                LowerPriorityTo = templateToCopy.LowerPriorityTo,
                UseMoonAvoidance = templateToCopy.UseMoonAvoidance,
                MoonAvoidanceProfilesJson = templateToCopy.MoonAvoidanceProfilesJson,
                MinStartTime = templateToCopy.MinStartTime,
                MaxStartTime = templateToCopy.MaxStartTime,
                MinMoonPhasePercent = templateToCopy.MinMoonPhasePercent,
                MaxMoonPhasePercent = templateToCopy.MaxMoonPhasePercent,
                DisplayOrder = templateToCopy.DisplayOrder
            };
            
            var result = await _apiClient.CreateSchedulerTargetTemplateAsync(copyDto);
            if (result != null)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    SchedulerTargetTemplates.Add(result);
                });
                SelectedSchedulerTargetTemplate = result;
                ConnectionStatus = "Template copied";
                ConnectionStatusColor = WpfBrushes.Green;
                RaisePropertyChanged(nameof(SchedulerTargetTemplateCount));
            }
            else
            {
                ConnectionStatus = "Failed to copy template";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }
        
        #endregion
        
        #region Moon Avoidance Profile CRUD
        
        private async Task AddMoonAvoidanceProfileAsync()
        {
            var newProfile = new MoonAvoidanceProfileDto
            {
                Name = "New Profile",
                FullMoonDistanceDegrees = 60,
                WidthInDays = 7,
                MinMoonAltitudeDegrees = 0
            };
            
            var result = await _apiClient.CreateMoonAvoidanceProfileAsync(newProfile);
            if (result != null)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    MoonAvoidanceProfiles.Add(result);
                });
                SelectedMoonAvoidanceProfile = result;
                ConnectionStatus = "Moon avoidance profile created";
                ConnectionStatusColor = WpfBrushes.Green;
                RaisePropertyChanged(nameof(MoonAvoidanceProfileCount));
            }
            else
            {
                ConnectionStatus = "Failed to create profile";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }
        
        private async Task SaveMoonAvoidanceProfileAsync()
        {
            if (_selectedMoonAvoidanceProfile == null) return;
            
            var result = await _apiClient.UpdateMoonAvoidanceProfileAsync(_selectedMoonAvoidanceProfile);
            if (result != null)
            {
                _selectedMoonAvoidanceProfileBackup = null;
                ConnectionStatus = "Moon avoidance profile saved";
                ConnectionStatusColor = WpfBrushes.Green;
                SelectedMoonAvoidanceProfile = null;
            }
            else
            {
                ConnectionStatus = "Failed to save profile";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }
        
        private async Task DeleteMoonAvoidanceProfileAsync(MoonAvoidanceProfileDto? profile)
        {
            var profileToDelete = profile ?? _selectedMoonAvoidanceProfile;
            if (profileToDelete == null) return;
            
            if (profileToDelete.IsSystemDefault)
            {
                ConnectionStatus = "Cannot delete system default profile";
                ConnectionStatusColor = WpfBrushes.Orange;
                return;
            }
            
            ConfirmDialogTitle = "Delete Profile";
            ConfirmDialogMessage = $"Are you sure you want to delete '{profileToDelete.Name}'?";
            _confirmDialogAction = async () => await ExecuteDeleteMoonAvoidanceProfileAsync(profileToDelete);
            ShowConfirmDialog = true;
        }
        
        private async Task ExecuteDeleteMoonAvoidanceProfileAsync(MoonAvoidanceProfileDto profileToDelete)
        {
            var success = await _apiClient.DeleteMoonAvoidanceProfileAsync(profileToDelete.Id);
            if (success)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    MoonAvoidanceProfiles.Remove(profileToDelete);
                });
                if (_selectedMoonAvoidanceProfile?.Id == profileToDelete.Id)
                    SelectedMoonAvoidanceProfile = null;
                ConnectionStatus = "Moon avoidance profile deleted";
                ConnectionStatusColor = WpfBrushes.Green;
                RaisePropertyChanged(nameof(MoonAvoidanceProfileCount));
            }
            else
            {
                ConnectionStatus = "Failed to delete profile";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }
        
        private async Task CopyMoonAvoidanceProfileAsync(MoonAvoidanceProfileDto? profile)
        {
            var profileToCopy = profile ?? _selectedMoonAvoidanceProfile;
            if (profileToCopy == null) return;
            
            var copyDto = new MoonAvoidanceProfileDto
            {
                Name = $"{profileToCopy.Name} (Copy)",
                FullMoonDistanceDegrees = profileToCopy.FullMoonDistanceDegrees,
                WidthInDays = profileToCopy.WidthInDays,
                MinMoonAltitudeDegrees = profileToCopy.MinMoonAltitudeDegrees
            };
            
            var result = await _apiClient.CreateMoonAvoidanceProfileAsync(copyDto);
            if (result != null)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    MoonAvoidanceProfiles.Add(result);
                });
                SelectedMoonAvoidanceProfile = result;
                ConnectionStatus = "Moon avoidance profile copied";
                ConnectionStatusColor = WpfBrushes.Green;
                RaisePropertyChanged(nameof(MoonAvoidanceProfileCount));
            }
            else
            {
                ConnectionStatus = "Failed to copy profile";
                ConnectionStatusColor = WpfBrushes.Red;
            }
        }

        #endregion

        #region Image Statistics

        private async Task LoadImageStatsAsync(Guid targetId)
        {
            if (_settings.UseCachedTargetsOnConnectionLoss || string.IsNullOrEmpty(_settings.LicenseKey))
            {
                CapturedImageSummary = null;
                return;
            }
            
            try
            {
                var summary = await _apiClient.GetCapturedImageSummaryAsync(targetId);
                CapturedImageSummary = summary;
                
                RaisePropertyChanged(nameof(HasImageStats));
                RaisePropertyChanged(nameof(ImageStatsIntegration));
                RaisePropertyChanged(nameof(ImageStatsAvgFwhm));
                RaisePropertyChanged(nameof(ImageStatsBestFwhm));
                RaisePropertyChanged(nameof(ImageStatsAvgHfd));
                RaisePropertyChanged(nameof(ImageStatsAvgSnr));
                RaisePropertyChanged(nameof(ImageStatsStarCount));
                RaisePropertyChanged(nameof(ImageStatsEccentricity));
                RaisePropertyChanged(nameof(ImageStatsAcceptanceRate));
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManager: Failed to load image stats: {ex.Message}");
                CapturedImageSummary = null;
            }
        }

        #endregion

        #region Scheduler Preview (Load from API)

        private async Task LoadSchedulerPreviewAsync()
        {
            if (_settings.UseCachedTargetsOnConnectionLoss || string.IsNullOrEmpty(_settings.LicenseKey))
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    PreviewSessions.Clear();
                });
                RaisePropertyChanged(nameof(HasPreviewSessions));
                RaisePropertyChanged(nameof(PreviewSessionsCount));
                RaisePropertyChanged(nameof(PreviewTotalTime));
                return;
            }
            
            IsLoadingPreview = true;
            
            try
            {
                var sessions = await _apiClient.GetSessionsByDateAsync(_previewDate);
                
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    PreviewSessions.Clear();
                    if (sessions != null)
                    {
                        foreach (var session in sessions.OrderBy(s => s.StartTimeUtc))
                        {
                            PreviewSessions.Add(session);
                        }
                    }
                });
                
                RaisePropertyChanged(nameof(HasPreviewSessions));
                RaisePropertyChanged(nameof(PreviewSessionsCount));
                RaisePropertyChanged(nameof(PreviewTotalTime));
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManager: Failed to load scheduler preview: {ex.Message}");
            }
            finally
            {
                IsLoadingPreview = false;
            }
        }

        #endregion

        #region Scheduler Preview (Generate LOCALLY using shared algorithm)

        private async Task GenerateSchedulerPreviewAsync()
        {
            if (!SelectedPreviewConfigId.HasValue)
            {
                ConnectionStatus = "Please select a configuration";
                ConnectionStatusColor = WpfBrushes.Orange;
                return;
            }
            
            IsLoadingPreview = true;
            SchedulerPreview = null;
            
            try
            {
                ConnectionStatus = "Refreshing targets from API...";
                ConnectionStatusColor = WpfBrushes.Gray;
                await SyncTargetsAsync();
                
                ConnectionStatus = "Generating preview locally...";
                ConnectionStatusColor = WpfBrushes.Gray;
                Logger.Info($"GenerateSchedulerPreviewAsync: Starting local preview for config {SelectedPreviewConfigId.Value}");
                Logger.Info($"GenerateSchedulerPreviewAsync: PreviewDate={GeneratePreviewDate:yyyy-MM-dd}, Kind={GeneratePreviewDate.Kind}, Now={DateTime.Now:yyyy-MM-dd HH:mm}, UtcNow={DateTime.UtcNow:yyyy-MM-dd HH:mm}");
                
                var config = SchedulerConfigurations.FirstOrDefault(c => c.Id == SelectedPreviewConfigId.Value);
                if (config == null)
                {
                    Logger.Warning("GenerateSchedulerPreviewAsync: Configuration not found in cache");
                    ConnectionStatus = "Configuration not found";
                    ConnectionStatusColor = WpfBrushes.Red;
                    return;
                }
                Logger.Info($"GenerateSchedulerPreviewAsync: Using config '{config.Name}'");
                
                var ninaLat = _profileService.ActiveProfile.AstrometrySettings.Latitude;
                var ninaLon = _profileService.ActiveProfile.AstrometrySettings.Longitude;
                var ninaElev = _profileService.ActiveProfile.AstrometrySettings.Elevation;
                
                if (ninaLat == 0 && ninaLon == 0)
                {
                    Logger.Warning("GenerateSchedulerPreviewAsync: NINA location not configured");
                    ConnectionStatus = "NINA location not configured - check Options > General";
                    ConnectionStatusColor = WpfBrushes.Red;
                    return;
                }
                
                if (LicensedObservatory != null)
                {
                    var latDiff = Math.Abs(ninaLat - LicensedObservatory.Latitude);
                    var lonDiff = Math.Abs(ninaLon - LicensedObservatory.Longitude);
                    if (latDiff > 0.1 || lonDiff > 0.1)
                    {
                        LocationMismatchWarning = $"⚠ NINA location ({ninaLat:F2}°, {ninaLon:F2}°) differs from '{LicensedObservatory.Name}' ({LicensedObservatory.Latitude:F2}°, {LicensedObservatory.Longitude:F2}°). Using NINA settings.";
                        ShowLocationMismatch = true;
                        Logger.Warning($"Location mismatch: {LocationMismatchWarning}");
                    }
                    else
                    {
                        ShowLocationMismatch = false;
                    }
                }
                
                var observatory = new ObservatoryDto
                {
                    Id = Guid.Empty,
                    Name = "NINA Profile Location",
                    Latitude = ninaLat,
                    Longitude = ninaLon,
                    Elevation = ninaElev
                };
                Logger.Info($"GenerateSchedulerPreviewAsync: Using NINA location ({ninaLat:F4}, {ninaLon:F4}, {ninaElev}m)");
                
                var targetsToPreview = Targets.ToList();
                Logger.Info($"GenerateSchedulerPreviewAsync: Found {targetsToPreview.Count} targets ({targetsToPreview.Count(t => t.Status == Shared.Model.Enums.ScheduledTargetStatus.Active)} active)");
                
                if (!targetsToPreview.Any())
                {
                    ConnectionStatus = "No targets for preview";
                    ConnectionStatusColor = WpfBrushes.Orange;
                    var emptyPreview = new SchedulerPreviewDto { Success = true, ErrorMessage = "No targets", Sessions = new List<SchedulerPreviewSessionDto>() };
                    SchedulerPreview = emptyPreview;
                    return;
                }
                
                var astronomyLogger = new NinaLogger<Shared.Services.Astronomy.AASharpAstronomyService>();
                var astronomyService = new Shared.Services.Astronomy.AASharpAstronomyService(astronomyLogger);
                var schedulerLogger = new NinaLogger<Shared.Services.Scheduler.SchedulingAlgorithmService>();
                var schedulingAlgorithm = new Shared.Services.Scheduler.SchedulingAlgorithmService(astronomyService, schedulerLogger);
                
                var moonProfiles = new List<UserFilterMoonAvoidanceProfileDto>();
                if (ExposureTemplates != null && MoonAvoidanceProfiles != null)
                {
                    foreach (var template in ExposureTemplates.Where(t => t.MoonAvoidanceProfileId.HasValue))
                    {
                        var profile = MoonAvoidanceProfiles.FirstOrDefault(p => p.Id == template.MoonAvoidanceProfileId);
                        if (profile != null)
                        {
                            var existingMapping = moonProfiles.FirstOrDefault(m => m.Filter == template.Filter);
                            if (existingMapping == null)
                            {
                                moonProfiles.Add(new UserFilterMoonAvoidanceProfileDto
                                {
                                    Filter = template.Filter,
                                    MoonAvoidanceProfileId = profile.Id,
                                    MoonAvoidanceProfile = profile
                                });
                                Logger.Debug($"GenerateSchedulerPreviewAsync: Filter {template.Filter} -> Moon avoidance profile '{profile.Name}'");
                            }
                        }
                    }
                    Logger.Info($"GenerateSchedulerPreviewAsync: Built {moonProfiles.Count} filter->moon avoidance profile mappings from exposure templates");
                }
                
                Logger.Info($"GenerateSchedulerPreviewAsync: Calling local algorithm...");
                
                MeridianFlipSettingsDto? meridianFlipSettings = null;
                try
                {
                    var mfSettings = _profileService.ActiveProfile?.MeridianFlipSettings;
                    if (mfSettings != null)
                    {
                        var isEnabled = mfSettings.MinutesAfterMeridian > 0 || mfSettings.PauseTimeBeforeMeridian > 0;
                        if (isEnabled)
                        {
                            meridianFlipSettings = new MeridianFlipSettingsDto
                            {
                                Enabled = true,
                                MinutesAfterMeridian = mfSettings.MinutesAfterMeridian,
                                PauseTimeBeforeFlipMinutes = mfSettings.PauseTimeBeforeMeridian,
                                MaxMinutesToMeridian = mfSettings.MinutesAfterMeridian,
                                UseSiderealTime = mfSettings.UseSideOfPier
                            };
                            Logger.Info($"GenerateSchedulerPreviewAsync: Meridian flip settings - {mfSettings.MinutesAfterMeridian}min after, {mfSettings.PauseTimeBeforeMeridian}min pause before, UseSideOfPier={mfSettings.UseSideOfPier}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"GenerateSchedulerPreviewAsync: Could not read meridian flip settings: {ex.Message}");
                }
                
                var preview = await schedulingAlgorithm.GeneratePreviewAsync(
                    targetsToPreview,
                    config,
                    observatory,
                    moonProfiles,
                    GeneratePreviewDate,
                    meridianFlipSettings,
                    includeDetailedExplanations: true,
                    includeAltitudeData: true,
                    altitudeDataIntervalMinutes: 15);
                
                SchedulerPreview = preview;
                
                Logger.Info($"GenerateSchedulerPreviewAsync: Preview result - Success={preview.Success}, Sessions={preview.Sessions?.Count ?? 0}, SkippedTargets={preview.SkippedTargets?.Count ?? 0}");
                Logger.Info($"GenerateSchedulerPreviewAsync: Twilight - Dusk={preview.AstronomicalDusk:HH:mm}, Dawn={preview.AstronomicalDawn:HH:mm}");
                
                if (preview.SkippedTargets?.Any() == true)
                {
                    Logger.Info($"GenerateSchedulerPreviewAsync: {preview.SkippedTargets.Count} targets were not scheduled:");
                    foreach (var skipped in preview.SkippedTargets)
                    {
                        var altDataCount = skipped.AltitudeData?.Count ?? 0;
                        Logger.Info($"  - {skipped.TargetName}: {skipped.Reason} (MaxAlt={skipped.MaxAltitude:F1}°, AltDataPoints={altDataCount})");
                        if (!string.IsNullOrEmpty(skipped.DetailedExplanation))
                            Logger.Debug($"    Details: {skipped.DetailedExplanation}");
                    }
                }
                
                if (preview.Sessions?.Any() == true)
                {
                    foreach (var session in preview.Sessions)
                    {
                        Logger.Info($"  Session: {session.TargetName} {session.StartTimeUtc:HH:mm}-{session.EndTimeUtc:HH:mm}");
                    }
                }
                
                if (preview.Success)
                {
                    Logger.Info($"GenerateSchedulerPreviewAsync: Success - {preview.Sessions?.Count ?? 0} sessions generated");
                    ConnectionStatus = $"Preview generated: {preview.Sessions?.Count ?? 0} sessions";
                    ConnectionStatusColor = WpfBrushes.Green;
                }
                else
                {
                    Logger.Warning($"GenerateSchedulerPreviewAsync: Failed - {preview.ErrorMessage}");
                    ConnectionStatus = $"Preview failed: {preview.ErrorMessage}";
                    ConnectionStatusColor = WpfBrushes.Red;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error generating scheduler preview: {ex.Message}");
                Logger.Error(ex.StackTrace);
                ConnectionStatus = $"Error: {ex.Message}";
                ConnectionStatusColor = WpfBrushes.Red;
            }
            finally
            {
                IsLoadingPreview = false;
            }
        }

        #endregion

        #region Panel Update Methods
        
        private async Task SavePanelEnabledStateAsync()
        {
            if (_selectedPanel == null || _selectedTarget == null) return;
            
            if (!string.IsNullOrEmpty(_settings.LicenseKey))
            {
                var success = await _apiClient.UpdatePanelAsync(_selectedPanel);
                if (success)
                {
                    ConnectionStatus = $"Panel {_selectedPanel.PanelNumber} {(_selectedPanel.IsEnabled ? "enabled" : "disabled")}";
                    ConnectionStatusColor = WpfBrushes.Green;
                }
                else
                {
                    ConnectionStatus = "⚠️ No connection - panel update failed!";
                    ConnectionStatusColor = WpfBrushes.Orange;
                }
            }
        }
        
        private async Task SavePanelShootingOrderAsync(ScheduledTargetPanelDto? swappedPanel = null)
        {
            if (_selectedPanel == null || _selectedTarget == null) return;
            
            if (!string.IsNullOrEmpty(_settings.LicenseKey))
            {
                var success = await _apiClient.UpdatePanelAsync(_selectedPanel);
                
                if (swappedPanel != null)
                {
                    await _apiClient.UpdatePanelAsync(swappedPanel);
                }
                
                if (success)
                {
                    var orderDisplay = _selectedPanel.ShootingOrder.HasValue ? $"#{_selectedPanel.ShootingOrder}" : "default";
                    var swapInfo = swappedPanel != null ? $" (swapped with P{swappedPanel.PanelNumber})" : "";
                    ConnectionStatus = $"Panel {_selectedPanel.PanelNumber} order set to {orderDisplay}{swapInfo}";
                    ConnectionStatusColor = WpfBrushes.Green;
                }
                else
                {
                    ConnectionStatus = "⚠️ No connection - panel update failed!";
                    ConnectionStatusColor = WpfBrushes.Orange;
                }
            }
        }
        
        #endregion

        #region Queue Management
        
        private async Task RefreshQueueAsync()
        {
            if (string.IsNullOrEmpty(ConnectionStatus) || ConnectionStatus.Contains("Not connected")) return;
            
            try
            {
                IsLoadingQueue = true;
                
                var queue = await _apiClient.GetTargetQueueAsync();
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    TargetQueue.Clear();
                    if (queue != null)
                    {
                        foreach (var item in queue.OrderBy(q => q.QueueOrder))
                        {
                            TargetQueue.Add(item);
                        }
                    }
                });
                
                var (success, _, serverTargets) = await _apiClient.SyncScheduledTargetsAsync();
                if (success && serverTargets != null && serverTargets.Count > 0)
                {
                    _targetStore.UpdateTargets(serverTargets);
                    RefreshTargetsList();
                    Logger.Debug($"AstroManager: RefreshQueue - synced {serverTargets.Count} targets from server");
                }
                
                var queuedIds = TargetQueue.Select(q => q.ScheduledTargetId).ToHashSet();
                var allTargets = _targetStore.GetAllTargets();
                Logger.Debug($"AstroManager: RefreshQueue - allTargets count: {allTargets.Count}, queuedIds count: {queuedIds.Count}");
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    AvailableTargets.Clear();
                    foreach (var target in allTargets.Where(t => !queuedIds.Contains(t.Id)).OrderBy(t => t.Name))
                    {
                        AvailableTargets.Add(target);
                    }
                    Logger.Debug($"AstroManager: RefreshQueue - AvailableTargets count: {AvailableTargets.Count}");
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Error refreshing queue: {ex.Message}");
            }
            finally
            {
                IsLoadingQueue = false;
            }
        }
        
        private async Task AddSelectedToQueueAsync()
        {
            if (SelectedAvailableTarget == null) return;
            
            try
            {
                var result = await _apiClient.AddToQueueAsync(SelectedAvailableTarget.Id);
                if (result != null)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        TargetQueue.Add(result);
                        AvailableTargets.Remove(SelectedAvailableTarget);
                        SelectedAvailableTarget = null;
                    });
                    Logger.Info($"AstroManager: Added target to queue: {result.TargetName}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Error adding to queue: {ex.Message}");
            }
        }
        
        private async Task RemoveSelectedFromQueueAsync()
        {
            if (SelectedQueueItem == null) return;
            
            try
            {
                var item = SelectedQueueItem;
                var success = await _apiClient.RemoveFromQueueAsync(item.Id);
                if (success)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        TargetQueue.Remove(item);
                        var target = _targetStore.GetAllTargets().FirstOrDefault(t => t.Id == item.ScheduledTargetId);
                        if (target != null && !AvailableTargets.Any(t => t.Id == target.Id))
                        {
                            AvailableTargets.Add(target);
                        }
                        SelectedQueueItem = null;
                        int order = 1;
                        foreach (var q in TargetQueue.OrderBy(x => x.QueueOrder))
                        {
                            q.QueueOrder = order++;
                        }
                    });
                    Logger.Info($"AstroManager: Removed target from queue: {item.TargetName}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Error removing from queue: {ex.Message}");
            }
        }
        
        private async Task MoveQueueItemUpAsync()
        {
            if (SelectedQueueItem == null || SelectedQueueItem.QueueOrder <= 1) return;
            
            try
            {
                var reorderedIds = TargetQueue.OrderBy(q => q.QueueOrder).Select(q => q.Id).ToList();
                var currentIndex = reorderedIds.IndexOf(SelectedQueueItem.Id);
                if (currentIndex > 0)
                {
                    reorderedIds.RemoveAt(currentIndex);
                    reorderedIds.Insert(currentIndex - 1, SelectedQueueItem.Id);
                    
                    var selectedId = SelectedQueueItem.Id;
                    var success = await _apiClient.ReorderQueueAsync(reorderedIds);
                    if (success)
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            var items = TargetQueue.ToList();
                            foreach (var item in items)
                            {
                                item.QueueOrder = reorderedIds.IndexOf(item.Id) + 1;
                            }
                            TargetQueue.Clear();
                            foreach (var item in items.OrderBy(i => i.QueueOrder))
                            {
                                TargetQueue.Add(item);
                            }
                            SelectedQueueItem = TargetQueue.FirstOrDefault(q => q.Id == selectedId);
                            RaisePropertyChanged(nameof(CanMoveUp));
                            RaisePropertyChanged(nameof(CanMoveDown));
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Error moving queue item up: {ex.Message}");
            }
        }
        
        private async Task MoveQueueItemDownAsync()
        {
            if (SelectedQueueItem == null || SelectedQueueItem.QueueOrder >= TargetQueue.Count) return;
            
            try
            {
                var reorderedIds = TargetQueue.OrderBy(q => q.QueueOrder).Select(q => q.Id).ToList();
                var currentIndex = reorderedIds.IndexOf(SelectedQueueItem.Id);
                if (currentIndex < reorderedIds.Count - 1)
                {
                    reorderedIds.RemoveAt(currentIndex);
                    reorderedIds.Insert(currentIndex + 1, SelectedQueueItem.Id);
                    
                    var selectedId = SelectedQueueItem.Id;
                    var success = await _apiClient.ReorderQueueAsync(reorderedIds);
                    if (success)
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            var items = TargetQueue.ToList();
                            foreach (var item in items)
                            {
                                item.QueueOrder = reorderedIds.IndexOf(item.Id) + 1;
                            }
                            TargetQueue.Clear();
                            foreach (var item in items.OrderBy(i => i.QueueOrder))
                            {
                                TargetQueue.Add(item);
                            }
                            SelectedQueueItem = TargetQueue.FirstOrDefault(q => q.Id == selectedId);
                            RaisePropertyChanged(nameof(CanMoveUp));
                            RaisePropertyChanged(nameof(CanMoveDown));
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Error moving queue item down: {ex.Message}");
            }
        }
        
        private async Task ClearQueueAsync()
        {
            if (TargetQueue.Count == 0) return;
            
            try
            {
                var success = await _apiClient.ClearQueueAsync();
                if (success)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        TargetQueue.Clear();
                        AvailableTargets.Clear();
                        foreach (var target in _targetStore.GetAllTargets().OrderBy(t => t.Name))
                        {
                            AvailableTargets.Add(target);
                        }
                    });
                    Logger.Info("AstroManager: Cleared target queue");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Error clearing queue: {ex.Message}");
            }
        }
        
        #endregion
    }
}
