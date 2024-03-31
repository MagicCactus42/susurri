using System.ComponentModel.DataAnnotations;

namespace Susurri.Client.Models;

public class SignUpViewModel
{
    [Required]
    public string Username { get; set; }

    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; }
}