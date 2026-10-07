using AmaanParkingSystem.Services;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace AmaanParkingSystem.Controllers
{
    public class BackupController : BaseController
    {
        private readonly DatabaseBackupService _backupService;
        private readonly ILogger<BackupController> _logger;

        public BackupController(DatabaseBackupService backupService, ILogger<BackupController> logger)
        {
            _backupService = backupService;
            _logger = logger;
        }

        // GET: /Backup/Index
        public async Task<IActionResult> Index()
        {
            var history = await _backupService.GetBackupHistoryAsync();
            ViewBag.BackupHistory = history.backups;
            return View();
        }

        // POST: /Backup/CreateManualBackup
        [HttpPost]
        public async Task<IActionResult> CreateManualBackup()
        {
            try
            {
                var result = await _backupService.CreateBackupAsync();

                return Json(new
                {
                    success = result.success,
                    message = result.message,
                    filePath = result.filePath
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Manual backup failed: {ex.Message}");
                return Json(new
                {
                    success = false,
                    message = $"Backup failed: {ex.Message}"
                });
            }
        }

        // GET: /Backup/GetBackupHistory
        [HttpGet]
        public async Task<IActionResult> GetBackupHistory()
        {
            var result = await _backupService.GetBackupHistoryAsync();
            return Json(new
            {
                success = result.success,
                message = result.message,
                backups = result.backups
            });
        }
    }
}
