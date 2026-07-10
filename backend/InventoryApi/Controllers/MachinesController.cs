using InventoryApi.Data;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MachinesController(AppDbContext db, INayaxLynxClient nayaxLynxClient) : ControllerBase
    {

        [HttpGet]
        public async Task<ActionResult<Machine>> GetAll()
        {
            var nayaxMachines = await nayaxLynxClient.GetMachinesAsync();

            var machines = nayaxMachines.Select(x => new Machine
            {
                ActorID = x.ActorID,
                MachineID = x.MachineID,
                MachineName = x.MachineName,
                MachineNumber = x.MachineNumber
            });

            return Ok(machines);
        }

    }
}
