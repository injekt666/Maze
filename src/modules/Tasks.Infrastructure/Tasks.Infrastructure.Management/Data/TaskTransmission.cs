﻿using System;
using System.ComponentModel.DataAnnotations;

namespace Tasks.Infrastructure.Management.Data
{
    public class TaskTransmission
    {
        [Key]
        public int TaskTransmissionId { get; set; }
        public string TaskSessionId { get; set; }

        public int? TargetId { get; set; }
        public DateTimeOffset CreatedOn { get; set; }
    }
}