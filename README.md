# external-sorting using .net

This application uses Two-Way Sorting. Below there is description of number sorting as example. 
Application uses row as text and number intead of number in example.
Application has to consiquence of two step, where first one is creating input file and the next is sorting.
User should enter data (number of files to merge in one big file and number of rows in file) that will be used for generation of input file. 
Next step is enter sorting data ( number of tapes and length of run (length of sorted portion)).

Here is an example of Two-Way Sorting.
Where N is number of input file. M is run.

N = 14, M = 3 (14 records on tape Ta1, memory capacity: 3 records.)

Ta1: 17, 3, 29, 56, 24, 18, 4, 9, 10, 6, 45, 36, 11, 43

Sorting of runs:
Read 3 records in main memory, sort them and store them on Tb1:
17, 3, 29 -> 3, 17, 29

Tb1: 3, 17, 29

Read the next 3 records in main memory, sort them and store them on Tb2
56, 24, 18 -> 18, 24, 56

Tb2: 18, 24, 56

Read the next 3 records in main memory, sort them and store them on Tb1

4, 9, 10 -> 4, 9, 10
Tb1: 3, 17, 29, 4, 9, 10

Read the next 3 records in main memory, sort them and store them on Tb2
6, 45, 36 -> 6, 36, 45

Tb2: 18, 24, 56, 6, 36, 45

Read the next 3 records in main memory, sort them and store them on Tb1

(there are only two records left)
11, 43 -> 11, 43
Tb1: 3, 17, 29, 4, 9, 10, 11, 43

At the end of this process we will have three runs on Tb1 and two runs on Tb2:

Tb1: 3, 17, 29 | 4, 9, 10 | 11, 43

Tb2: 18, 24, 56 | 6, 36, 45 |

Merging of runs
B1. Merging runs of length 3 to obtain runs of length 6. 

Source tapes: Tb1 and Tb2, result on Ta1 and Ta2.
Merge the first two runs (on Tb1 and Tb2) and store the result on Ta1.

Tb1: 3, 17, 29 | 4, 9, 10 | 11, 43

Tb2: 18, 24, 56 | 6, 36, 45 |


Ta1: 3, 17, 18, 24, 29, 56 |

Ta2: 4, 6, 9, 10, 36, 45 | 11, 43

Merging runs of length 6 to obtain runs of length 12. 

Tb1: 3, 4, 6, 9, 10, 17, 18, 24, 29, 36, 45, 56 |

Tb2: 11, 43

result : Ta1: 3, 4, 6, 9, 10, 11, 17, 18, 24, 29, 36, 43, 45, 56 |

In each pass the size of the runs is doubled, thus we need [log(N/M)]+1 to get to a run equal in size to the original file.
This run would be the entire file sorted.



