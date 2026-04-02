using NINA.Core.Utility;
using Shared.Model.DTO.Client;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Local SQLite-based storage for offline operation.
    /// Stores offline token and queued capture data for sync when back online.
    /// </summary>
    public class OfflineDataStore : IDisposable
    {
        private readonly string _dbPath;
        private SQLiteConnection? _connection;
        private bool _disposed;
        
        public OfflineDataStore()
        {
            // Store in NINA's plugin data folder
            var pluginDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "Plugins", "AstroManager");
            
            Directory.CreateDirectory(pluginDataPath);
            _dbPath = Path.Combine(pluginDataPath, "offline_data.db");
        }
        
        /// <summary>
        /// Initialize the database and create tables if needed
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                var connectionString = $"Data Source={_dbPath};Version=3;";
                _connection = new SQLiteConnection(connectionString);
                await _connection.OpenAsync();
                
                // Create tables
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS OfflineToken (
                        Id INTEGER PRIMARY KEY,
                        LicenseId TEXT NOT NULL,
                        UserId TEXT NOT NULL,
                        IssuedAt TEXT NOT NULL,
                        ExpiresAt TEXT NOT NULL,
                        MachineFingerprint TEXT NOT NULL,
                        Signature TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL
                    );
                    
                    CREATE TABLE IF NOT EXISTS CaptureQueue (
                        Id TEXT PRIMARY KEY,
                        CapturedAt TEXT NOT NULL,
                        TargetId TEXT,
                        ImagingGoalId TEXT,
                        PanelId TEXT,
                        Filter TEXT,
                        ExposureTimeSeconds REAL,
                        Success INTEGER NOT NULL,
                        FileName TEXT,
                        HFR REAL,
                        DetectedStars INTEGER,
                        CameraTemp REAL,
                        Gain INTEGER,
                        IsSynced INTEGER NOT NULL DEFAULT 0,
                        SyncAttempts INTEGER NOT NULL DEFAULT 0,
                        LastSyncError TEXT,
                        CreatedAt TEXT NOT NULL
                    );
                    
                    CREATE INDEX IF NOT EXISTS IX_CaptureQueue_IsSynced ON CaptureQueue(IsSynced);
                ";
                await cmd.ExecuteNonQueryAsync();
                
                Logger.Info("OfflineDataStore: Database initialized at " + _dbPath);
            }
            catch (Exception ex)
            {
                Logger.Error($"OfflineDataStore: Failed to initialize database: {ex.Message}");
                throw;
            }
        }
        
        #region Offline Token
        
        /// <summary>
        /// Save offline token to local storage
        /// </summary>
        public async Task SaveOfflineTokenAsync(OfflineTokenDto token)
        {
            if (_connection == null) await InitializeAsync();
            
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO OfflineToken (Id, LicenseId, UserId, IssuedAt, ExpiresAt, MachineFingerprint, Signature, UpdatedAt)
                    VALUES (1, @LicenseId, @UserId, @IssuedAt, @ExpiresAt, @MachineFingerprint, @Signature, @UpdatedAt)
                ";
                cmd.Parameters.AddWithValue("@LicenseId", token.LicenseId.ToString());
                cmd.Parameters.AddWithValue("@UserId", token.UserId.ToString());
                cmd.Parameters.AddWithValue("@IssuedAt", token.IssuedAt.ToString("O"));
                cmd.Parameters.AddWithValue("@ExpiresAt", token.ExpiresAt.ToString("O"));
                cmd.Parameters.AddWithValue("@MachineFingerprint", token.MachineFingerprint);
                cmd.Parameters.AddWithValue("@Signature", token.Signature);
                cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("O"));
                
                await cmd.ExecuteNonQueryAsync();
                Logger.Debug($"OfflineDataStore: Saved offline token, expires at {token.ExpiresAt}");
            }
            catch (Exception ex)
            {
                Logger.Error($"OfflineDataStore: Failed to save offline token: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Load offline token from local storage
        /// </summary>
        public async Task<OfflineTokenDto?> LoadOfflineTokenAsync()
        {
            if (_connection == null) await InitializeAsync();
            
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT LicenseId, UserId, IssuedAt, ExpiresAt, MachineFingerprint, Signature FROM OfflineToken WHERE Id = 1";
                
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    return new OfflineTokenDto
                    {
                        LicenseId = Guid.Parse(reader.GetString(0)),
                        UserId = Guid.Parse(reader.GetString(1)),
                        IssuedAt = DateTime.Parse(reader.GetString(2)),
                        ExpiresAt = DateTime.Parse(reader.GetString(3)),
                        MachineFingerprint = reader.GetString(4),
                        Signature = reader.GetString(5)
                    };
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"OfflineDataStore: Failed to load offline token: {ex.Message}");
                return null;
            }
        }
        
        #endregion
        
        #region Capture Queue
        
        /// <summary>
        /// Queue a capture for later sync
        /// </summary>
        public async Task QueueCaptureAsync(OfflineCaptureDto capture)
        {
            if (_connection == null) await InitializeAsync();
            
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO CaptureQueue (Id, CapturedAt, TargetId, ImagingGoalId, PanelId, Filter, 
                        ExposureTimeSeconds, Success, FileName, HFR, DetectedStars, CameraTemp, Gain, 
                        IsSynced, SyncAttempts, LastSyncError, CreatedAt)
                    VALUES (@Id, @CapturedAt, @TargetId, @ImagingGoalId, @PanelId, @Filter,
                        @ExposureTimeSeconds, @Success, @FileName, @HFR, @DetectedStars, @CameraTemp, @Gain,
                        0, 0, NULL, @CreatedAt)
                ";
                cmd.Parameters.AddWithValue("@Id", capture.Id.ToString());
                cmd.Parameters.AddWithValue("@CapturedAt", capture.CapturedAt.ToString("O"));
                cmd.Parameters.AddWithValue("@TargetId", capture.TargetId?.ToString() ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ImagingGoalId", capture.ImagingGoalId?.ToString() ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@PanelId", capture.PanelId?.ToString() ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Filter", capture.Filter ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ExposureTimeSeconds", capture.ExposureTimeSeconds ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Success", capture.Success ? 1 : 0);
                cmd.Parameters.AddWithValue("@FileName", capture.FileName ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@HFR", capture.HFR ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@DetectedStars", capture.DetectedStars ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@CameraTemp", capture.CameraTemp ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Gain", capture.Gain ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("O"));
                
                await cmd.ExecuteNonQueryAsync();
                Logger.Debug($"OfflineDataStore: Queued capture {capture.Id} for target {capture.TargetId}");
            }
            catch (Exception ex)
            {
                Logger.Error($"OfflineDataStore: Failed to queue capture: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get all unsynced captures
        /// </summary>
        public async Task<List<OfflineCaptureDto>> GetUnsyncedCapturesAsync(int maxCount = 100)
        {
            if (_connection == null) await InitializeAsync();
            
            var captures = new List<OfflineCaptureDto>();
            
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, CapturedAt, TargetId, ImagingGoalId, PanelId, Filter, ExposureTimeSeconds,
                           Success, FileName, HFR, DetectedStars, CameraTemp, Gain, SyncAttempts, LastSyncError
                    FROM CaptureQueue 
                    WHERE IsSynced = 0 
                    ORDER BY CapturedAt ASC
                    LIMIT @MaxCount
                ";
                cmd.Parameters.AddWithValue("@MaxCount", maxCount);
                
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    captures.Add(new OfflineCaptureDto
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        CapturedAt = DateTime.Parse(reader.GetString(1)),
                        TargetId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                        ImagingGoalId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                        PanelId = reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                        Filter = reader.IsDBNull(5) ? null : reader.GetString(5),
                        ExposureTimeSeconds = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                        Success = reader.GetInt32(7) == 1,
                        FileName = reader.IsDBNull(8) ? null : reader.GetString(8),
                        HFR = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                        DetectedStars = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                        CameraTemp = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                        Gain = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                        SyncAttempts = reader.GetInt32(13),
                        LastSyncError = reader.IsDBNull(14) ? null : reader.GetString(14)
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"OfflineDataStore: Failed to get unsynced captures: {ex.Message}");
            }
            
            return captures;
        }
        
        /// <summary>
        /// Mark captures as synced
        /// </summary>
        public async Task MarkCapturesSyncedAsync(IEnumerable<Guid> captureIds)
        {
            if (_connection == null || !captureIds.Any()) return;
            
            try
            {
                using var cmd = _connection!.CreateCommand();
                var idList = string.Join(",", captureIds.Select(id => $"'{id}'"));
                cmd.CommandText = $"UPDATE CaptureQueue SET IsSynced = 1 WHERE Id IN ({idList})";
                await cmd.ExecuteNonQueryAsync();
                
                Logger.Info($"OfflineDataStore: Marked {captureIds.Count()} captures as synced");
            }
            catch (Exception ex)
            {
                Logger.Error($"OfflineDataStore: Failed to mark captures synced: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Update sync attempt count and error for a capture
        /// </summary>
        public async Task UpdateSyncAttemptAsync(Guid captureId, string? error)
        {
            if (_connection == null) return;
            
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    UPDATE CaptureQueue 
                    SET SyncAttempts = SyncAttempts + 1, LastSyncError = @Error
                    WHERE Id = @Id
                ";
                cmd.Parameters.AddWithValue("@Id", captureId.ToString());
                cmd.Parameters.AddWithValue("@Error", error ?? (object)DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                Logger.Error($"OfflineDataStore: Failed to update sync attempt: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get count of unsynced captures
        /// </summary>
        public async Task<int> GetUnsyncedCountAsync()
        {
            if (_connection == null) await InitializeAsync();
            
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM CaptureQueue WHERE IsSynced = 0";
                var result = await cmd.ExecuteScalarAsync();
                return Convert.ToInt32(result);
            }
            catch
            {
                return 0;
            }
        }
        
        /// <summary>
        /// Delete old synced captures (cleanup)
        /// </summary>
        public async Task CleanupOldSyncedCapturesAsync(int keepDays = 7)
        {
            if (_connection == null) return;
            
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-keepDays).ToString("O");
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "DELETE FROM CaptureQueue WHERE IsSynced = 1 AND CapturedAt < @Cutoff";
                cmd.Parameters.AddWithValue("@Cutoff", cutoff);
                var deleted = await cmd.ExecuteNonQueryAsync();
                
                if (deleted > 0)
                {
                    Logger.Info($"OfflineDataStore: Cleaned up {deleted} old synced captures");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"OfflineDataStore: Failed to cleanup old captures: {ex.Message}");
            }
        }
        
        #endregion
        
        public void Dispose()
        {
            if (_disposed) return;
            _connection?.Close();
            _connection?.Dispose();
            _disposed = true;
        }
    }
}
