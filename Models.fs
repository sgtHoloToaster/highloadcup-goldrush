module Models

type License = {
    Id: int
    DigAllowed: int
    DigUsed: int
} 

type Area = {
    PosX: int
    PosY: int
    SizeX: int
    SizeY: int
}

type Dig = {
    LicenseID: int
    PosX: int
    PosY: int
    Depth: int
}

let oneBlockArea: Area = { PosX = 0; PosY = 0; SizeX = 1; SizeY = 1 }

type Explore = {
    Priority: int
    Area: Area
    Amount: int
}

type Wallet = {
    Balance: int
    Wallet: int seq
}

type Treasure = {
    Priority: int
    Treasures: string seq
}