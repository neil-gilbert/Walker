"""Shared construction of control/candidate Walker benchmark commands."""
import os


def walker_command(label, executable, arguments, candidate_arguments, host='dotnet', apphost=False, control_arguments=()):
    if label not in ('walker', 'before'):
        raise ValueError('Unknown benchmark label: ' + label)
    prefix = [str(executable)[:-4] + ('.exe' if os.name == 'nt' else '')] if apphost and str(executable).endswith('.dll') else [str(host), str(executable)]
    return [*prefix, *arguments,
            *(candidate_arguments if label == 'walker' else control_arguments)]
