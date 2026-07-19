using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MPR.Application.DTOs;
using MPR.Application.Interfaces;

namespace MPR.Web.Controllers;

[ApiController]
[Route("api/export")]
[Authorize(Policy = "CanExportOrEmail")]
public class ExportController : ControllerBase
{
    private readonly IExcelExportService _excel;
    private readonly IEmailService _email;

    public ExportController(IExcelExportService excel, IEmailService email)
    {
        _excel = excel;
        _email = email;
    }

    [HttpGet("periods/{id}/excel")]
    public async Task<IActionResult> DownloadExcel(int id)
    {
        var stream = await _excel.ExportAsync(id);
        var fileName = $"MPR_{id}.xlsx";
        return File(stream, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    [HttpPost("email")]
    public async Task<IActionResult> Email(EmailReportRequest req)
    {
        var stream = await _excel.ExportAsync(req.ReportPeriodId);
        var fileName = $"MPR_{req.ReportPeriodId}.xlsx";
        var ok = await _email.SendReportAsync(req.ReportPeriodId, req.Recipients, req.Subject, req.Message, stream, fileName);
        return ok ? Ok() : StatusCode(500, "Email send failed - check EmailLog for details.");
    }
}
