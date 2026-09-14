using BPM.Application.Departments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/departments")]
public class DepartmentsController : ControllerBase
{
    private readonly IDepartmentService _departmentService;

    public DepartmentsController(IDepartmentService departmentService)
    {
        _departmentService = departmentService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DepartmentDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _departmentService.GetAllAsync(cancellationToken));

    [HttpPost]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<DepartmentDto>> Create(CreateDepartmentRequest request, CancellationToken cancellationToken) =>
        Ok(await _departmentService.CreateAsync(request, cancellationToken));
}
